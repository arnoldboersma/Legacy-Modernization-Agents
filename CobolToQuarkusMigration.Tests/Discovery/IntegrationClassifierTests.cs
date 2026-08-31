using CobolToQuarkusMigration.Discovery.Integrations;
using CobolToQuarkusMigration.Discovery.Models;
using CobolToQuarkusMigration.Discovery.Roles;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Discovery;

/// <summary>
/// Tests for the phase 6 (issue #6) deterministic integration inventory classifier: enumeration
/// per category, classification correctness (runtime/platform-identity/observability/delivery),
/// evidence/citation/confidence recording, and blind-spot handling for the explicitly flagged
/// naming/path-based heuristics (design doc §14).
/// </summary>
public sealed class IntegrationClassifierTests
{
    [Fact]
    public void ClassifyProject_ControllerType_EmitsInboundHttpApiRuntimeApplication()
    {
        var file = new ClassifierSourceFile("Api/WidgetsController.cs", @"
using Microsoft.AspNetCore.Mvc;
namespace App.Api;
[ApiController]
[Route(""api/[controller]"")]
public class WidgetsController : ControllerBase
{
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        results.Should().ContainSingle(r =>
            r.Category == IntegrationCategory.HttpApi &&
            r.Classification == IntegrationClassification.RuntimeApplication &&
            r.Direction == IntegrationDirection.Inbound &&
            r.LogicalTarget == "WidgetsController" &&
            !r.RequiresReview);
    }

    [Fact]
    public void ClassifyProject_MessagingUsingDirective_EmitsMessagingRuntimeApplication()
    {
        var file = new ClassifierSourceFile("Messaging/BusPublisher.cs", @"
using Azure.Messaging.ServiceBus;
namespace App.Messaging;
public class BusPublisher
{
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        results.Should().ContainSingle(r =>
            r.Category == IntegrationCategory.Messaging &&
            r.Classification == IntegrationClassification.RuntimeApplication &&
            r.ProtocolOrMechanism == "Azure.Messaging.ServiceBus");
    }

    [Fact]
    public void ClassifyProject_IdentityUsingDirective_EmitsPlatformIdentityWithAuthSemantics()
    {
        var file = new ClassifierSourceFile("Auth/GraphClientFactory.cs", @"
using Microsoft.Graph;
using Azure.Identity;
namespace App.Auth;
public class GraphClientFactory
{
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        results.Should().Contain(r =>
            r.Category == IntegrationCategory.IdentityOrAuthorization &&
            r.Classification == IntegrationClassification.PlatformIdentity &&
            r.AuthenticationSemantics != null && r.AuthenticationSemantics.Contains("Entra"));
    }

    [Fact]
    public void ClassifyProject_ObservabilityUsingDirective_EmitsObservabilityClassification()
    {
        var file = new ClassifierSourceFile("Telemetry/Startup.cs", @"
using OpenTelemetry;
namespace App.Telemetry;
public class Startup
{
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        results.Should().ContainSingle(r => r.Classification == IntegrationClassification.Observability);
    }

    [Fact]
    public void ClassifyProject_DbContext_EmitsDatabaseOrSharedStoreRuntimeApplication()
    {
        var file = new ClassifierSourceFile("Data/ApplicationContext.cs", @"
using Microsoft.EntityFrameworkCore;
namespace App.Data;
public class ApplicationContext : DbContext
{
    public DbSet<Widget> Widgets { get; set; }
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        results.Should().ContainSingle(r =>
            r.Category == IntegrationCategory.DatabaseOrSharedStore &&
            r.Classification == IntegrationClassification.RuntimeApplication &&
            r.ClassificationRule == "EfDbContextBaseType" &&
            !r.RequiresReview);
    }

    [Fact]
    public void ClassifyProject_BackgroundWorker_WithoutDeliveryPathSegment_DefaultsToRuntimeApplication_ButRequiresReview()
    {
        var file = new ClassifierSourceFile("App.Worker/ReminderWorker.cs", @"
using Microsoft.Extensions.Hosting;
namespace App.Worker;
public class ReminderWorker : BackgroundService
{
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        var candidate = results.Should().ContainSingle(r => r.Category == IntegrationCategory.ScheduledOrBackgroundProcess).Subject;
        candidate.Classification.Should().Be(IntegrationClassification.RuntimeApplication);
        candidate.RequiresReview.Should().BeTrue("the Delivery-vs-runtime split for background workers is a flagged path-based heuristic");
        candidate.BlindSpots.Should().NotBeEmpty();
    }

    [Fact]
    public void ClassifyProject_BackgroundWorker_WithMigrationPathSegment_ClassifiesAsDeliveryAndFlagsBlindSpot()
    {
        var file = new ClassifierSourceFile("App.DatabaseMigration/Worker.cs", @"
using Microsoft.Extensions.Hosting;
namespace App.DatabaseMigration;
public class Worker : BackgroundService
{
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        var candidate = results.Should().ContainSingle(r => r.Category == IntegrationCategory.ScheduledOrBackgroundProcess).Subject;
        candidate.Classification.Should().Be(IntegrationClassification.Delivery);
        candidate.RequiresReview.Should().BeTrue();
        candidate.BlindSpots.Should().ContainSingle(b => b.Contains("path", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClassifyProject_NotificationOutboxTypeNameShape_EmitsNotificationCategoryAndFlagsBlindSpot()
    {
        var file = new ClassifierSourceFile("Notifications/AcsEmailDeliveryClient.cs", @"
namespace App.Notifications;
public class AcsEmailDeliveryClient
{
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        var candidate = results.Should().ContainSingle(r => r.ClassificationRule == "NotificationOrOutboxTypeNameShape").Subject;
        candidate.Category.Should().Be(IntegrationCategory.Notification);
        candidate.RequiresReview.Should().BeTrue();
        candidate.BlindSpots.Should().NotBeEmpty();
    }

    [Fact]
    public void ClassifyProject_NotificationOutboxRepository_EmitsMessagingCategoryWithReliabilityBehavior()
    {
        var file = new ClassifierSourceFile("Notifications/NotificationOutboxRepository.cs", @"
namespace App.Notifications;
public class NotificationOutboxRepository
{
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        var candidate = results.Should().ContainSingle(r => r.ClassificationRule == "NotificationOrOutboxTypeNameShape").Subject;
        candidate.Category.Should().Be(IntegrationCategory.Messaging);
        candidate.ReliabilityBehavior.Should().Contain("Outbox pattern");
    }

    [Fact]
    public void ClassifyProject_ConstructorInjectsIHttpClientFactory_EmitsOutboundHttpApiAndFlagsBlindSpot()
    {
        var file = new ClassifierSourceFile("Controllers/AvailabilityController.cs", @"
using System.Net.Http;
namespace App.Controllers;
public class AvailabilityController
{
    public AvailabilityController(IHttpClientFactory httpClientFactory)
    {
    }
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        var candidate = results.Should().ContainSingle(r => r.ClassificationRule == "HttpClientFactoryOrHttpClientConsumer").Subject;
        candidate.Category.Should().Be(IntegrationCategory.HttpApi);
        candidate.Classification.Should().Be(IntegrationClassification.RuntimeApplication);
        candidate.Direction.Should().Be(IntegrationDirection.Outbound);
        candidate.RequiresReview.Should().BeTrue();
        candidate.BlindSpots.Should().ContainSingle(b => b.Contains("destination host/service"));
    }

    [Fact]
    public void ClassifyProject_FieldOfTypeHttpClient_EmitsOutboundHttpApiCandidate()
    {
        var file = new ClassifierSourceFile("Clients/DownstreamClient.cs", @"
using System.Net.Http;
namespace App.Clients;
public class DownstreamClient
{
    private readonly HttpClient _httpClient;
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        var candidate = results.Should().ContainSingle(r => r.ClassificationRule == "HttpClientFactoryOrHttpClientConsumer").Subject;
        candidate.Direction.Should().Be(IntegrationDirection.Outbound);
        candidate.LogicalTarget.Should().Be("DownstreamClient");
    }

    [Fact]
    public void ClassifyProject_ControllerWithoutHttpClientDependency_EmitsNoOutboundHttpApiCandidate()
    {
        var file = new ClassifierSourceFile("Api/WidgetsController.cs", @"
using Microsoft.AspNetCore.Mvc;
namespace App.Api;
[Route(""api/widgets"")]
public class WidgetsController : ControllerBase
{
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        results.Should().NotContain(r => r.ClassificationRule == "HttpClientFactoryOrHttpClientConsumer");
    }

    [Fact]
    public void ClassifyProject_PlainClass_EmitsNoIntegrationCandidates()
    {
        var file = new ClassifierSourceFile("Domain/Widget.cs", @"
namespace App.Domain;
public class Widget
{
    public int Id { get; set; }
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        results.Should().BeEmpty();
    }

    [Fact]
    public void ClassifyProject_EveryCandidate_HasNonEmptyCitations()
    {
        var file = new ClassifierSourceFile("Api/WidgetsController.cs", @"
using Microsoft.AspNetCore.Mvc;
namespace App.Api;
[Route(""api/widgets"")]
public class WidgetsController : ControllerBase
{
}");

        var results = IntegrationClassifier.ClassifyProject(new[] { file });

        results.Should().NotBeEmpty();
        results.Should().OnlyContain(r => r.Citations.Count > 0 && r.Confidence > 0 && r.Confidence <= 1.0);
    }

    [Fact]
    public void ClassifyConfigurationFile_ConnectionStringsKey_EmitsDatabaseCategory_KeyNameOnly_NeverValue()
    {
        var file = new ClassifierSourceFile("appsettings.json", @"{
  ""ConnectionStrings"": {
    ""ApplicationDb"": ""Server=tcp:example;Database=App;Password=SuperSecret123;""
  }
}");

        var results = IntegrationClassifier.ClassifyConfigurationFile(file);

        var candidate = results.Should().ContainSingle(r => r.Category == IntegrationCategory.DatabaseOrSharedStore).Subject;
        candidate.Classification.Should().Be(IntegrationClassification.RuntimeApplication);
        candidate.ConfigurationKeySemantics.Should().Be("ConnectionStrings:ApplicationDb");
        candidate.ConfigurationKeySemantics.Should().NotContain("SuperSecret123");
        candidate.Citations.Should().OnlyContain(c => !c.Excerpt.Contains("SuperSecret123"));
    }

    [Fact]
    public void ClassifyConfigurationFile_KeyVaultNameKey_EmitsPlatformIdentityServiceDiscoveryCategory()
    {
        var file = new ClassifierSourceFile("appsettings.json", @"{ ""KeyVaultName"": ""contoso-kv-prod"" }");

        var results = IntegrationClassifier.ClassifyConfigurationFile(file);

        var candidate = results.Should().ContainSingle(r => r.Category == IntegrationCategory.ServiceDiscoveryOrPlatformConfig).Subject;
        candidate.Classification.Should().Be(IntegrationClassification.PlatformIdentity);
        candidate.ConfigurationKeySemantics.Should().Be("KeyVaultName");
        candidate.ConfigurationKeySemantics.Should().NotContain("contoso-kv-prod");
    }

    [Fact]
    public void ClassifyConfigurationFile_NoRecognizedKeys_EmitsNoCandidates()
    {
        var file = new ClassifierSourceFile("appsettings.json", @"{ ""Logging"": { ""LogLevel"": { ""Default"": ""Information"" } } }");

        var results = IntegrationClassifier.ClassifyConfigurationFile(file);

        results.Should().BeEmpty();
    }
}
