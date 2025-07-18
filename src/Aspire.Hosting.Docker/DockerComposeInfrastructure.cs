#pragma warning disable ASPIRECOMPUTE001
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker;

/// <summary>
/// Represents the infrastructure for Docker Compose within the Aspire Hosting environment.
/// Implements the <see cref="IDistributedApplicationLifecycleHook"/> interface to provide lifecycle hooks for distributed applications.
/// </summary>
internal sealed class DockerComposeInfrastructure(
    ILogger<DockerComposeInfrastructure> logger,
    DistributedApplicationExecutionContext executionContext) : IDistributedApplicationLifecycleHook
{
    public async Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
    {
        if (executionContext.IsRunMode)
        {
            return;
        }

        // Find Docker Compose environment resources
        var dockerComposeEnvironments = appModel.Resources.OfType<DockerComposeEnvironmentResource>().ToArray();

        if (dockerComposeEnvironments.Length == 0)
        {
            EnsureNoPublishAsDockerComposeServiceAnnotations(appModel);
            return;
        }

        foreach (var environment in dockerComposeEnvironments)
        {
            var dockerComposeEnvironmentContext = new DockerComposeEnvironmentContext(environment, logger);

            if (environment.DashboardEnabled && environment.Dashboard?.Resource is DockerComposeAspireDashboardResource dashboard)
            {
                // Ensure the dashboard resource is created (even though it's not part of the main application model)
                var dashboardService = await dockerComposeEnvironmentContext.CreateDockerComposeServiceResourceAsync(dashboard, executionContext, cancellationToken).ConfigureAwait(false);

                dashboard.Annotations.Add(new DeploymentTargetAnnotation(dashboardService)
                {
                    ComputeEnvironment = environment
                });
            }

            foreach (var r in appModel.GetComputeResources())
            {
                // Configure OTLP for resources if dashboard is enabled (before creating the service resource)
                if (environment.DashboardEnabled && environment.Dashboard?.Resource.OtlpGrpcEndpoint is EndpointReference otlpGrpcEndpoint)
                {
                    ConfigureOtlp(r, otlpGrpcEndpoint);
                }

                // Create a Docker Compose compute resource for the resource
                var serviceResource = await dockerComposeEnvironmentContext.CreateDockerComposeServiceResourceAsync(r, executionContext, cancellationToken).ConfigureAwait(false);

                // Add deployment target annotation to the resource
                r.Annotations.Add(new DeploymentTargetAnnotation(serviceResource)
                {
                    ComputeEnvironment = environment
                });
            }
        }
    }

    private static void EnsureNoPublishAsDockerComposeServiceAnnotations(DistributedApplicationModel appModel)
    {
        foreach (var r in appModel.GetComputeResources())
        {
            if (r.HasAnnotationOfType<DockerComposeServiceCustomizationAnnotation>())
            {
                throw new InvalidOperationException($"Resource '{r.Name}' is configured to publish as a Docker Compose service, but there are no '{nameof(DockerComposeEnvironmentResource)}' resources. Ensure you have added one by calling '{nameof(DockerComposeEnvironmentExtensions.AddDockerComposeEnvironment)}'.");
            }
        }
    }

    private static void ConfigureOtlp(IResource resource, EndpointReference otlpEndpoint)
    {
        // Only configure OTLP for resources that have the OtlpExporterAnnotation and implement IResourceWithEnvironment
        if (resource is IResourceWithEnvironment resourceWithEnv && resource.Annotations.OfType<OtlpExporterAnnotation>().Any())
        {
            // Configure OTLP environment variables
            resourceWithEnv.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
            {
                context.EnvironmentVariables["OTEL_EXPORTER_OTLP_ENDPOINT"] = otlpEndpoint;
                context.EnvironmentVariables["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc";
                context.EnvironmentVariables["OTEL_SERVICE_NAME"] = resource.Name;
                return Task.CompletedTask;
            }));
        }
    }
}
