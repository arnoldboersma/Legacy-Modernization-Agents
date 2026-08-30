using CobolToQuarkusMigration.Chunking.Adapters;
using CobolToQuarkusMigration.Chunking.Interfaces;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Chunking.Adapters;

public sealed class CobolAdapterTests
{
    private readonly CobolAdapter _adapter = new();

    [Fact]
    public void CanProcess_RecognizesSupportedExtensionsAndCobolContentOnly()
    {
        _adapter.CanProcess("PAYROLL.CBL").Should().BeTrue();
        _adapter.CanProcess("copybook.cpy").Should().BeTrue();
        _adapter.CanProcess("legacy.cobol").Should().BeTrue();
        _adapter.CanProcess("unknown.txt", "       DATA DIVISION.").Should().BeTrue();

        _adapter.CanProcess("Worker.cs", "public sealed class Worker { }").Should().BeFalse();
        _adapter.CanProcess("notes.txt").Should().BeFalse();
    }

    [Fact]
    public async Task IdentifySemanticUnitsAsync_ExtractsDivisionSectionParagraphAndDependencies()
    {
        var source = string.Join('\n',
            "       IDENTIFICATION DIVISION.",
            "       PROGRAM-ID. SAMPLE.",
            "       ENVIRONMENT DIVISION.",
            "       DATA DIVISION.",
            "       WORKING-STORAGE SECTION.",
            "       01 CUSTOMER-RECORD.",
            "       05 CUSTOMER-ID PIC X(10).",
            "       PROCEDURE DIVISION.",
            "       MAIN-SECTION SECTION.",
            "       MAIN-PARA.",
            "           PERFORM PROCESS-PARA.",
            "       PROCESS-PARA.",
            "           DISPLAY 'DONE'.");

        var units = await _adapter.IdentifySemanticUnitsAsync(source, "sample.cbl");

        units.Select(unit => unit.Id).Should().Equal(
            "DIVISION:IDENTIFICATION-DIVISION",
            "DIVISION:ENVIRONMENT-DIVISION",
            "SECTION:WORKING-STORAGE",
            "DIVISION:DATA-DIVISION",
            "DIVISION:PROCEDURE-DIVISION",
            "SECTION:MAIN-SECTION",
            "PARAGRAPH:MAIN-PARA",
            "PARAGRAPH:PROCESS-PARA");

        var mainParagraph = units.Single(unit => unit.Id == "PARAGRAPH:MAIN-PARA");
        mainParagraph.ParentId.Should().Be("SECTION:MAIN-SECTION");
        mainParagraph.StartLine.Should().Be(10);
        mainParagraph.EndLine.Should().Be(11);
        mainParagraph.Dependencies.Should().Equal("PROCESS-PARA");
        mainParagraph.EstimatedTokens.Should().Be(mainParagraph.Content.Length / 4);

        var processParagraph = units.Single(unit => unit.Id == "PARAGRAPH:PROCESS-PARA");
        processParagraph.Dependents.Should().Equal("MAIN-PARA");
        processParagraph.LineCount.Should().Be(2);
    }

    [Fact]
    public async Task ExtractVariablesAsync_PreservesHierarchyTypesValuesAndSourceOrder()
    {
        var source = string.Join('\n',
            "       01 CUSTOMER-RECORD.",
            "       05 CUSTOMER-ID PIC X(10) VALUE 'UNKNOWN'.",
            "       05 BALANCE PICTURE 9(7)V99.",
            "       77 ORPHAN PIC 9.");

        var variables = await _adapter.ExtractVariablesAsync(source);

        variables.Select(variable => variable.LegacyName).Should().Equal(
            "CUSTOMER-RECORD", "CUSTOMER-ID", "BALANCE", "ORPHAN");
        variables[0].Should().BeEquivalentTo(new
        {
            LegacyName = "CUSTOMER-RECORD",
            LegacyType = "GROUP",
            Level = (int?)1,
            ParentName = (string?)null,
            LineNumber = 1,
            IsGroup = true
        });
        variables[1].Should().BeEquivalentTo(new
        {
            LegacyName = "CUSTOMER-ID",
            LegacyType = "PIC X(10)",
            Level = (int?)5,
            ParentName = "CUSTOMER-RECORD",
            LineNumber = 2,
            InitialValue = "'UNKNOWN'",
            IsGroup = false
        });
        variables[2].LegacyType.Should().Be("PIC 9(7)V99");
        variables[2].InitialValue.Should().BeNull();
        variables[3].ParentName.Should().Be("CUSTOMER-RECORD");
    }

    [Fact]
    public async Task ExtractCallDependenciesAsync_TracksParagraphCallerAndCallKinds()
    {
        var source = string.Join('\n',
            "       MAIN-PARA.",
            "           PERFORM WORK-PARA.",
            "           CALL 'EXTERNAL-PROG'.",
            "           GO TO EXIT-PARA.");

        var dependencies = await _adapter.ExtractCallDependenciesAsync(source);

        dependencies.Should().SatisfyRespectively(
            dependency =>
            {
                dependency.CallerName.Should().Be("MAIN-PARA");
                dependency.CalledName.Should().Be("WORK-PARA");
                dependency.LineNumber.Should().Be(2);
                dependency.CallType.Should().Be("PERFORM");
                dependency.IsExternal.Should().BeFalse();
            },
            dependency =>
            {
                dependency.CallerName.Should().Be("MAIN-PARA");
                dependency.CalledName.Should().Be("EXTERNAL-PROG");
                dependency.LineNumber.Should().Be(3);
                dependency.CallType.Should().Be("CALL");
                dependency.IsExternal.Should().BeTrue();
            },
            dependency =>
            {
                dependency.CalledName.Should().Be("EXIT-PARA");
                dependency.LineNumber.Should().Be(4);
                dependency.CallType.Should().Be("GO TO");
                dependency.IsExternal.Should().BeFalse();
            });
    }

    [Fact]
    public async Task ExtractExternalReferencesAsync_ExtractsOptionalLibraryAndKeepsUnqualifiedCopybook()
    {
        const string source = """
            COPY CUSTOMER-DATA OF COMMON-LIB.
            COPY ADDRESS.CPY.
            """;

        var references = await _adapter.ExtractExternalReferencesAsync(source);

        references.Should().SatisfyRespectively(
            reference =>
            {
                reference.FileName.Should().Be("CUSTOMER-DATA");
                reference.LineNumber.Should().Be(1);
                reference.ReferenceType.Should().Be("COPY");
                reference.Library.Should().Be("COMMON-LIB");
            },
            reference =>
            {
                reference.FileName.Should().Be("ADDRESS.CPY.");
                reference.LineNumber.Should().Be(2);
                reference.ReferenceType.Should().Be("COPY");
                reference.Library.Should().BeNull();
            });

        (await _adapter.ExtractExternalReferencesAsync(string.Empty)).Should().BeEmpty();
    }

    [Fact]
    public void ConvertNameDeterministic_UsesNameTypeSpecificCasingAndHandlesEmptyInput()
    {
        _adapter.ConvertNameDeterministic("  CUSTOMER-ACCOUNT  ", NameType.Variable)
            .Should().Be("customerAccount");
        _adapter.ConvertNameDeterministic("CUSTOMER-ACCOUNT", NameType.Class)
            .Should().Be("CustomerAccount");
        _adapter.ConvertNameDeterministic("CUSTOMER-ACCOUNT", NameType.Constant)
            .Should().Be("CUSTOMER_ACCOUNT");
        _adapter.ConvertNameDeterministic(string.Empty, NameType.Method)
            .Should().BeEmpty();
    }
}
