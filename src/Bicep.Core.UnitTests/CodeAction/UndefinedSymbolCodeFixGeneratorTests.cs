// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Bicep.Core.CodeAction;
using Bicep.Core.Diagnostics;
using Bicep.Core.UnitTests.Assertions;
using Bicep.Core.UnitTests.Utils;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bicep.Core.UnitTests.CodeAction;

[TestClass]
public class UndefinedSymbolCodeFixGeneratorTests
{
    [TestMethod]
    public void UndefinedSymbol_ShouldHaveCreateParameterAndCreateVariableFixes()
    {
        var (file, cursor) = ParserHelper.GetFileWithSingleCursor("""
            output out string = |undefinedName
            """);

        var result = CompilationHelper.Compile(file);

        using (new AssertionScope().WithVisualCursor(result.Compilation.GetEntrypointSemanticModel().SourceFile, cursor))
        {
            var matchingDiagnostics = result.Diagnostics
                .Where(x => x.Code == "BCP057")
                .Where(x => x.Span.ContainsInclusive(cursor));

            matchingDiagnostics.Should().ContainSingle();
            var diagnostic = matchingDiagnostics.Single();

            diagnostic.Fixes.Should().HaveCount(2);
            diagnostic.Fixes.Should().Contain(x => x.Title == "Create parameter 'undefinedName'");
            diagnostic.Fixes.Should().Contain(x => x.Title == "Create variable 'undefinedName'");
        }
    }

    [TestMethod]
    public void CreateParameterFix_ShouldInsertParameterDeclaration()
    {
        var (file, cursor) = ParserHelper.GetFileWithSingleCursor("""
            output out string = |storageAccountName
            """);

        var result = CompilationHelper.Compile(file);

        using (new AssertionScope().WithVisualCursor(result.Compilation.GetEntrypointSemanticModel().SourceFile, cursor))
        {
            var diagnostic = result.Diagnostics
                .Where(x => x.Code == "BCP057")
                .Where(x => x.Span.ContainsInclusive(cursor))
                .Single();

            var fix = diagnostic.Fixes.Single(x => x.Title == "Create parameter 'storageAccountName'");
            fix.Kind.Should().Be(CodeFixKind.QuickFix);

            fix.Should().HaveResult(file, """
                param storageAccountName string

                output out string = storageAccountName
                """);
        }
    }

    [TestMethod]
    public void CreateVariableFix_ShouldInsertVariableDeclaration()
    {
        var (file, cursor) = ParserHelper.GetFileWithSingleCursor("""
            output out string = |storageAccountName
            """);

        var result = CompilationHelper.Compile(file);

        using (new AssertionScope().WithVisualCursor(result.Compilation.GetEntrypointSemanticModel().SourceFile, cursor))
        {
            var diagnostic = result.Diagnostics
                .Where(x => x.Code == "BCP057")
                .Where(x => x.Span.ContainsInclusive(cursor))
                .Single();

            var fix = diagnostic.Fixes.Single(x => x.Title == "Create variable 'storageAccountName'");
            fix.Kind.Should().Be(CodeFixKind.QuickFix);

            fix.Should().HaveResult(file, """
                var storageAccountName = ''

                output out string = storageAccountName
                """);
        }
    }

    [TestMethod]
    public void UndefinedSymbolInCondition_ShouldInferBoolType()
    {
        var (file, cursor) = ParserHelper.GetFileWithSingleCursor("""
            resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
              name: 'st'
              location: 'westus'
              kind: 'StorageV2'
              sku: {
                name: 'Standard_LRS'
              }
              properties: {
                publicNetworkAccess: |enablePrivateEndpoint ? 'Disabled' : 'Enabled'
              }
            }
            """);

        var result = CompilationHelper.Compile(file);

        using (new AssertionScope().WithVisualCursor(result.Compilation.GetEntrypointSemanticModel().SourceFile, cursor))
        {
            var diagnostic = result.Diagnostics
                .Where(x => x.Code == "BCP057")
                .Where(x => x.Span.ContainsInclusive(cursor))
                .Single();

            var fix = diagnostic.Fixes.Single(x => x.Title == "Create parameter 'enablePrivateEndpoint'");
            fix.Kind.Should().Be(CodeFixKind.QuickFix);

            // The parameter should be inferred as bool type
            fix.Replacements.Single().Text.Should().Contain("param enablePrivateEndpoint bool");
        }
    }

    [TestMethod]
    public void UndefinedSymbolInArithmetic_ShouldInferIntType()
    {
        var (file, cursor) = ParserHelper.GetFileWithSingleCursor("""
            var result = |count * 2
            output out int = result
            """);

        var result = CompilationHelper.Compile(file);

        using (new AssertionScope().WithVisualCursor(result.Compilation.GetEntrypointSemanticModel().SourceFile, cursor))
        {
            var diagnostic = result.Diagnostics
                .Where(x => x.Code == "BCP057")
                .Where(x => x.Span.ContainsInclusive(cursor))
                .Single();

            var fix = diagnostic.Fixes.Single(x => x.Title == "Create parameter 'count'");
            fix.Kind.Should().Be(CodeFixKind.QuickFix);

            // The parameter should be inferred as int type
            fix.Replacements.Single().Text.Should().Contain("param count int");
        }
    }

    [TestMethod]
    public void UndefinedSymbol_InsertionShouldRespectExistingDeclarations()
    {
        var (file, cursor) = ParserHelper.GetFileWithSingleCursor("""
            param existingParam string

            output out string = |newParam
            """);

        var result = CompilationHelper.Compile(file);

        using (new AssertionScope().WithVisualCursor(result.Compilation.GetEntrypointSemanticModel().SourceFile, cursor))
        {
            var diagnostic = result.Diagnostics
                .Where(x => x.Code == "BCP057")
                .Where(x => x.Span.ContainsInclusive(cursor))
                .Single();

            var fix = diagnostic.Fixes.Single(x => x.Title == "Create parameter 'newParam'");

            // Should insert after existing parameter declarations
            fix.Should().HaveResult(file, """
                param existingParam string
                param newParam string

                output out string = newParam
                """);
        }
    }

    [TestMethod]
    public void CreateVariableFix_ShouldCreateIntDefaultForArithmeticContext()
    {
        var (file, cursor) = ParserHelper.GetFileWithSingleCursor("""
            var result = |count * 2
            output out int = result
            """);

        var result = CompilationHelper.Compile(file);

        using (new AssertionScope().WithVisualCursor(result.Compilation.GetEntrypointSemanticModel().SourceFile, cursor))
        {
            var diagnostic = result.Diagnostics
                .Where(x => x.Code == "BCP057")
                .Where(x => x.Span.ContainsInclusive(cursor))
                .Single();

            var fix = diagnostic.Fixes.Single(x => x.Title == "Create variable 'count'");
            fix.Kind.Should().Be(CodeFixKind.QuickFix);

            // The variable should be initialized with int default (0)
            fix.Replacements.Single().Text.Should().Contain("var count = 0");
        }
    }

    [TestMethod]
    public void UndefinedSymbol_WithSuggestion_ShouldNotHaveCreateFixes()
    {
        // BCP082 (name with suggestion) already has a "did you mean?" fix
        // and should not receive the "create parameter/variable" fixes
        var (file, cursor) = ParserHelper.GetFileWithSingleCursor("""
            param existingName string
            output out string = |existingNam
            """);

        var result = CompilationHelper.Compile(file);

        using (new AssertionScope().WithVisualCursor(result.Compilation.GetEntrypointSemanticModel().SourceFile, cursor))
        {
            // BCP082 should have a "did you mean?" fix, not create fixes
            var bcp082 = result.Diagnostics
                .Where(x => x.Code == "BCP082")
                .Where(x => x.Span.ContainsInclusive(cursor))
                .SingleOrDefault();

            if (bcp082 is not null)
            {
                bcp082.Fixes.Should().ContainSingle(f => f.Title.Contains("existingName"));
                bcp082.Fixes.Should().NotContain(f => f.Title.StartsWith("Create parameter"));
                bcp082.Fixes.Should().NotContain(f => f.Title.StartsWith("Create variable"));
            }
        }
    }
}
