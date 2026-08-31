using CobolToQuarkusMigration.Discovery.Graph;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Discovery;

public sealed class FrameworkNamespacesTests
{
    [Theory]
    [InlineData("Microsoft.Extensions.Hosting")]
    [InlineData("Microsoft.Extensions.Hosting.Internal")]
    [InlineData("Microsoft.AspNetCore.Mvc")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("System")]
    [InlineData("System.Text.Json")]
    [InlineData("Azure.Identity")]
    public void IsFrameworkNamespace_KnownFrameworkRoots_ReturnsTrue(string ns)
    {
        FrameworkNamespaces.IsFrameworkNamespace(ns).Should().BeTrue();
    }

    [Theory]
    [InlineData("App.Forecast")]
    [InlineData("Planbordv2.Worker")]
    [InlineData("SystemIntegration.Forecast")] // must not false-positive-match "System" as a prefix
    public void IsFrameworkNamespace_NonFrameworkNamespaces_ReturnsFalse(string ns)
    {
        FrameworkNamespaces.IsFrameworkNamespace(ns).Should().BeFalse();
    }

    [Fact]
    public void IsFrameworkNamespace_NullOrEmpty_ReturnsFalse()
    {
        FrameworkNamespaces.IsFrameworkNamespace(null).Should().BeFalse();
        FrameworkNamespaces.IsFrameworkNamespace(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void GetNamespace_ExtractsDeclaringNamespaceFromSymbolLocator()
    {
        FrameworkNamespaces.GetNamespace("Microsoft.Extensions.Hosting.InstrumentationSource")
            .Should().Be("Microsoft.Extensions.Hosting");
    }

    [Fact]
    public void GetNamespace_NoDot_ReturnsEmpty()
    {
        FrameworkNamespaces.GetNamespace("TopLevelType").Should().Be(string.Empty);
    }
}
