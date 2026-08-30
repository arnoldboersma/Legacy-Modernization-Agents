using CobolToQuarkusMigration.Discovery.Redaction;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Discovery;

public class RedactionGuardTests
{
    [Fact]
    public void Apply_RedactsConnectionStringPassword()
    {
        var input = "jdbc:oracle:thin:@//db:1521/orcl?password=SuperSecret123 user=app";

        var result = RedactionGuard.Apply(input);

        result.WasRedacted.Should().BeTrue();
        result.SanitizedText.Should().NotContain("SuperSecret123");
    }

    [Fact]
    public void Apply_RedactsBearerToken()
    {
        var input = "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abcdefg.hijklmnop";

        var result = RedactionGuard.Apply(input);

        result.WasRedacted.Should().BeTrue();
        result.SanitizedText.Should().NotContain("eyJhbGciOiJIUzI1NiJ9");
    }

    [Fact]
    public void Apply_RedactsApiKeyAssignment()
    {
        var input = "api_key = \"AKIA1234567890ABCDEF\"";

        var result = RedactionGuard.Apply(input);

        result.WasRedacted.Should().BeTrue();
        result.SanitizedText.Should().NotContain("AKIA1234567890ABCDEF");
    }

    [Fact]
    public void Apply_RedactsAwsAccessKey()
    {
        var input = "AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE";

        var result = RedactionGuard.Apply(input);

        result.WasRedacted.Should().BeTrue();
        result.SanitizedText.Should().NotContain("AKIAIOSFODNN7EXAMPLE");
    }

    [Fact]
    public void Apply_RedactsPrivateKeyBlock()
    {
        var input = "-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAK...\n-----END RSA PRIVATE KEY-----";

        var result = RedactionGuard.Apply(input);

        result.WasRedacted.Should().BeTrue();
        result.SanitizedText.Should().NotContain("MIIBOgIBAAJBAK");
    }

    [Fact]
    public void Apply_RedactsEmailAddress()
    {
        var input = "Contact: jane.doe@example.com for support.";

        var result = RedactionGuard.Apply(input);

        result.WasRedacted.Should().BeTrue();
        result.SanitizedText.Should().NotContain("jane.doe@example.com");
    }

    [Fact]
    public void Apply_RedactsSsn()
    {
        var input = "SSN on file: 123-45-6789";

        var result = RedactionGuard.Apply(input);

        result.WasRedacted.Should().BeTrue();
        result.SanitizedText.Should().NotContain("123-45-6789");
    }

    [Fact]
    public void Apply_RedactsCreditCardNumber()
    {
        var input = "Card number 4111 1111 1111 1111 on file.";

        var result = RedactionGuard.Apply(input);

        result.WasRedacted.Should().BeTrue();
        result.SanitizedText.Should().NotContain("4111 1111 1111 1111");
    }

    [Fact]
    public void Apply_LeavesNeutralTextUnchanged()
    {
        var input = "PROCEDURE DIVISION found at line 24; 2 paragraph header(s) detected.";

        var result = RedactionGuard.Apply(input);

        result.WasRedacted.Should().BeFalse();
        result.SanitizedText.Should().Be(input);
    }

    [Fact]
    public void DescribeConfigurationKey_NeverIncludesResolvedValue()
    {
        var description = RedactionGuard.DescribeConfigurationKey("DB_PASSWORD", "database connection secret");

        description.Should().Contain("DB_PASSWORD");
        description.Should().NotContain("=");
        description.Should().NotMatchRegex(@"[A-Za-z0-9]{12,}"); // no long token/value-shaped content
    }
}
