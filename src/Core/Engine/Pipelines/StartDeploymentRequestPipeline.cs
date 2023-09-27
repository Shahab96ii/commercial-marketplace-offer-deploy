﻿using JenkinsNET.Models;
using JenkinsNET.Exceptions;
using MediatR;
using MediatR.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modm.Deployments;
using Modm.Engine.Jenkins.Client;
using Modm.Engine.Notifications;

namespace Modm.Engine.Pipelines
{
    /// <summary>
    /// The pipeline
    /// </summary>
    public class StartDeploymentRequestPipeline : Pipeline<StartDeploymentRequest, StartDeploymentResult>
    {
        public StartDeploymentRequestPipeline(IMediator mediator) : base(mediator)
        {
        }
    }

    public static class StartDeploymentRequestPipelineRegistration
    {
        public static MediatRServiceConfiguration AddStartDeploymentRequestPipeline(this MediatRServiceConfiguration c)
        {
            // add sub pipeline which is a dependency for establishing the definition
            c.AddCreateDeploymentDefinitionPipeline();

            c.RegisterServicesFromAssemblyContaining<StartDeploymentRequestHandler>();

            c.AddRequestPostProcessor<WriteDeploymentToDisk>();
            c.AddBehavior<SubmitDeployment>();
            c.AddBehavior<ReadDeploymentFromDisk>();
            return c;
        }
    }

    #region Pipeline

    /// <summary>
    /// Starts the deployment request
    /// </summary>
    public class StartDeploymentRequestHandler : IRequestHandler<StartDeploymentRequest, StartDeploymentResult>
    {
        readonly IMediator mediator;
        public StartDeploymentRequestHandler(IMediator mediator) => this.mediator = mediator;

        public async Task<StartDeploymentResult> Handle(StartDeploymentRequest request, CancellationToken cancellationToken)
        {
            var definition = await CreateDefinition(request, cancellationToken);

            return new StartDeploymentResult
            {
                Deployment = new Deployment
                {
                    Definition = definition
                }
            };
        }

        private async Task<DeploymentDefinition> CreateDefinition(StartDeploymentRequest request, CancellationToken cancellationToken)
            => await mediator.Send<DeploymentDefinition>(new CreateDeploymentDefinition(request), cancellationToken);
    }

    // #1
    public class ReadDeploymentFromDisk : IPipelineBehavior<StartDeploymentRequest, StartDeploymentResult>
    {
        private readonly DeploymentFile file;
        public ReadDeploymentFromDisk(DeploymentFile file) => this.file = file;

        public async Task<StartDeploymentResult> Handle(StartDeploymentRequest request, RequestHandlerDelegate<StartDeploymentResult> next, CancellationToken cancellationToken)
        {
            var result = await next();
            result.Deployment = await file.Read(cancellationToken);

            return result;
        }
    }

    // #2
    public class SubmitDeployment : IPipelineBehavior<StartDeploymentRequest, StartDeploymentResult>
    {
        private readonly IJenkinsClient client;
        private readonly IMediator mediator;
        private readonly ILogger<SubmitDeployment> logger;

        public SubmitDeployment(IJenkinsClient client, IMediator mediator, ILogger<SubmitDeployment> logger)
        {
            this.client = client;
            this.mediator = mediator;
            this.logger = logger;
        }

        public async Task<StartDeploymentResult> Handle(StartDeploymentRequest request, RequestHandlerDelegate<StartDeploymentResult> next, CancellationToken cancellationToken)
        {
            var result = await next();
            if (result.Errors == null)
            {
                result.Errors = new List<string>();
            }

            var deployment = result.Deployment;
            
            await UpdateStatus(deployment);

            bool isStartable = await IsStartable(deployment);
            if (!isStartable)
            {
                deployment.Id = -1;
                AddError(result, "Deployment is not startable");
                return result;
            }

            try
            {
                if (await TryToSubmit(deployment))
                {
                    await Publish(deployment, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                AddError(result, ex.Message);
                logger.LogError(ex, "Failure to submit to jenkins");
            }

            result.Deployment = deployment;

            return result;
        }

        private void AddError(StartDeploymentResult result, string error)
        {
            if (result.Errors == null)
            {
                result.Errors = new List<string>();
            }

            result.Errors.Add(error);
        }

        private async Task<bool> IsStartable(Deployment deployment)
        {
            try
            {
                var queueItems = await client.Queue.GetAllItemsAsync();

                if (queueItems.Any(item => item.Task?.Name == deployment.Definition.DeploymentType))
                {
                    logger.LogDebug($"Deployment {deployment.Definition.DeploymentType} is already in the queue.");
                    return false;
                }

                var lastBuild = await client.Builds.GetAsync<JenkinsBuildBase>(deployment.Definition.DeploymentType, "lastBuild");

                if (lastBuild == null)
                {
                    logger.LogWarning($"Unable to fetch last build details for {deployment.Definition.DeploymentType}");
                    return true;
                }

                if (lastBuild.Building.GetValueOrDefault(false) || !string.IsNullOrEmpty(lastBuild.Result))
                {
                    logger.LogDebug($"Deployment {deployment.Definition.DeploymentType} either is currently building or already completed.");
                    return false;
                }

                return true;
            }
            catch (JenkinsJobGetBuildException)
            {
                logger.LogWarning($"No previous builds found for {deployment.Definition.DeploymentType} due to a 404 response. Assuming the job is startable.");
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to check if the deployment is startable for {deployment.Definition.DeploymentType}");
                return false;
            }
        }


        private async Task Publish(Deployment deployment, CancellationToken cancellationToken)
        {
            await mediator.Publish(new DeploymentStarted
            {
                Id = deployment.Id,
                Name = deployment.Definition.DeploymentType
            }, cancellationToken);
        }

        private async Task<bool> TryToSubmit(Deployment deployment)
        {
            var response = await client.Jobs.BuildAsync(deployment.Definition.DeploymentType);
            var queueId = response.GetQueueItemNumber().GetValueOrDefault(0);

            var failedToEnqueue = queueId == 0;

            if (failedToEnqueue)
            {
                return false;
            }

            var queueItem = await client.Queue.GetItemAsync(queueId);
            var deploymentId = queueItem?.Executable?.Number;

            if (!deploymentId.HasValue)
            {
                return false;
            }

            deployment.Id = deploymentId.Value;
            deployment.Status = DeploymentStatus.Running;
            return true;
        }

        private async Task UpdateStatus(Deployment deployment)
        {
            if (deployment.Id == 0 || deployment.Status == DeploymentStatus.Undefined)
            {
                logger.LogDebug("Deployment id is 0 or status is undefined.");
                deployment.Status = DeploymentStatus.Undefined;
                return;
            }

            try
            {
                var build = await client.Builds.GetAsync<JenkinsBuildBase>(deployment.Definition.DeploymentType, deployment.Id.ToString());
                if (build == null)
                {
                    deployment.Status = DeploymentStatus.Undefined;
                    return;
                }
                deployment.Status = build.Result.ToLower();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get build information");
            }
        }
    }

    // #3
    public class WriteDeploymentToDisk : IRequestPostProcessor<StartDeploymentRequest, StartDeploymentResult>
    {
        private readonly DeploymentFile file;

        public WriteDeploymentToDisk(DeploymentFile file) => this.file = file;

        public async Task Process(
            StartDeploymentRequest request,
            StartDeploymentResult response,
            CancellationToken cancellationToken)
        {
            await file.Write(response.Deployment, cancellationToken);
        }
    }

    #endregion
}

