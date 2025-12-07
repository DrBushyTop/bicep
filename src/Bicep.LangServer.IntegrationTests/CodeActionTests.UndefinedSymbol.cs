// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Bicep.Core.Diagnostics;
using Bicep.Core.UnitTests;
using Bicep.Core.UnitTests.Utils;
using Bicep.LangServer.IntegrationTests.Assertions;
using Bicep.LangServer.IntegrationTests.Helpers;
using Bicep.LanguageServer.Extensions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Bicep.LangServer.IntegrationTests;

public partial class CodeActionTests : CodeActionTestBase
{
    [TestMethod]
    public async Task Undefined_name_should_offer_create_parameter_and_variable_quick_fixes()
    {
        const string missingName = "storageAccountName";
        var bicepFileContents = $"output out string = {missingName}";
        var bicepFilePath = FileHelper.SaveResultFile(TestContext, "main.bicep", bicepFileContents);
        var documentUri = DocumentUri.FromFileSystemPath(bicepFilePath);
        var uri = documentUri.ToUriEncoded();

        var files = new Dictionary<Uri, string>
        {
            [uri] = bicepFileContents,
        };

        var compilation = Services.BuildCompilation(files, uri);
        var diagnostics = compilation.GetEntrypointSemanticModel().GetAllDiagnostics();
        diagnostics.Should().ContainSingle(d => d.Code == "BCP057");
        TestContext.WriteLine("Diagnostics: " + string.Join(", ", diagnostics.Select(d => d.Code)));

        var bcp057 = diagnostics.Single(d => d.Code == "BCP057");
        var diagnosticRange = bcp057.ToRange(compilation.SourceFileGrouping.EntryPoint.LineStarts);

        var helper = await ServerWithBuiltInTypes.GetAsync();
        await helper.OpenFileOnceAsync(TestContext, bicepFileContents, documentUri);

        var codeActions = await helper.Client.RequestCodeAction(new CodeActionParams
        {
            TextDocument = new TextDocumentIdentifier(documentUri),
            Range = diagnosticRange,
        });

        codeActions.Should().NotBeNull();
        TestContext.WriteLine("Returned code actions: " + string.Join(", ", codeActions!.Select(x => x.CodeAction?.Title ?? "<command>")));

        var createParam = codeActions!.Single(x => x.CodeAction?.Title == $"Create parameter '{missingName}'");
        var createVar = codeActions!.Single(x => x.CodeAction?.Title == $"Create variable '{missingName}'");

        var bicepFile = new LanguageClientFile(documentUri, bicepFileContents);

        LspRefactoringHelper.ApplyCodeAction(bicepFile, createParam.CodeAction!)
            .Should()
            .HaveSourceText($$"""
                param storageAccountName string

                output out string = storageAccountName
                """);

        LspRefactoringHelper.ApplyCodeAction(bicepFile, createVar.CodeAction!)
            .Should()
            .HaveSourceText($$"""
                var storageAccountName = ''

                output out string = storageAccountName
                """);
    }

    [TestMethod]
    public async Task Undefined_name_used_in_condition_should_infer_bool_parameter()
    {
        const string missingName = "enablePrivateEndpoint";
        var bicepFileContents = """
resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
  name: 'st'
  location: 'westus'
  publicNetworkAccess: enablePrivateEndpoint ? 'Disabled' : 'Enabled'
}
""";
        var bicepFilePath = FileHelper.SaveResultFile(TestContext, "main.bicep", bicepFileContents);
        var documentUri = DocumentUri.FromFileSystemPath(bicepFilePath);
        var uri = documentUri.ToUriEncoded();

        var files = new Dictionary<Uri, string>
        {
            [uri] = bicepFileContents,
        };

        var compilation = Services.BuildCompilation(files, uri);
        var diagnostics = compilation.GetEntrypointSemanticModel().GetAllDiagnostics();
        diagnostics.Should().ContainSingle(d => d.Code == "BCP057");

        var bcp057 = diagnostics.Single(d => d.Code == "BCP057");
        var diagnosticRange = bcp057.ToRange(compilation.SourceFileGrouping.EntryPoint.LineStarts);

        var helper = await ServerWithBuiltInTypes.GetAsync();
        await helper.OpenFileOnceAsync(TestContext, bicepFileContents, documentUri);

        var codeActions = await helper.Client.RequestCodeAction(new CodeActionParams
        {
            TextDocument = new TextDocumentIdentifier(documentUri),
            Range = diagnosticRange,
        });

        codeActions.Should().NotBeNull();
        var titles = codeActions!.Select(x => x.CodeAction?.Title).Where(title => title is not null).ToList();
        titles.Should().Contain($"Create parameter '{missingName}'", "a bool-typed parameter quick fix should be offered for the undefined condition symbol");

        var createParam = codeActions!.SingleOrDefault(x => x.CodeAction?.Title == $"Create parameter '{missingName}'");
        createParam.Should().NotBeNull();

        var bicepFile = new LanguageClientFile(documentUri, bicepFileContents);
        LspRefactoringHelper.ApplyCodeAction(bicepFile, createParam!.CodeAction!)
            .Should()
            .HaveSourceText("""
param enablePrivateEndpoint bool

resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
  name: 'st'
  location: 'westus'
  publicNetworkAccess: enablePrivateEndpoint ? 'Disabled' : 'Enabled'
}
""");
    }

    [TestMethod]
    public async Task Undefined_name_used_in_arithmetic_should_infer_int_parameter()
    {
        const string missingName = "replicas";
        var bicepFileContents = """
output total int = replicas + 2
""";
        var bicepFilePath = FileHelper.SaveResultFile(TestContext, "main.bicep", bicepFileContents);
        var documentUri = DocumentUri.FromFileSystemPath(bicepFilePath);
        var uri = documentUri.ToUriEncoded();

        var files = new Dictionary<Uri, string>
        {
            [uri] = bicepFileContents,
        };

        var compilation = Services.BuildCompilation(files, uri);
        var diagnostics = compilation.GetEntrypointSemanticModel().GetAllDiagnostics();
        diagnostics.Should().ContainSingle(d => d.Code == "BCP057");

        var bcp057 = diagnostics.Single(d => d.Code == "BCP057");
        var diagnosticRange = bcp057.ToRange(compilation.SourceFileGrouping.EntryPoint.LineStarts);

        var helper = await ServerWithBuiltInTypes.GetAsync();
        await helper.OpenFileOnceAsync(TestContext, bicepFileContents, documentUri);

        var codeActions = await helper.Client.RequestCodeAction(new CodeActionParams
        {
            TextDocument = new TextDocumentIdentifier(documentUri),
            Range = diagnosticRange,
        });

        codeActions.Should().NotBeNull();
        var createParam = codeActions!.SingleOrDefault(x => x.CodeAction?.Title == $"Create parameter '{missingName}'");
        createParam.Should().NotBeNull();

        var bicepFile = new LanguageClientFile(documentUri, bicepFileContents);
        LspRefactoringHelper.ApplyCodeAction(bicepFile, createParam!.CodeAction!)
            .Should()
            .HaveSourceText("""
param replicas int

output total int = replicas + 2
""");
    }

    [TestMethod]
    public async Task Undefined_name_used_in_object_context_should_infer_object_parameter()
    {
        const string missingName = "sku";
        var bicepFileContents = """
resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
  name: 'st'
  location: 'westus'
  sku: sku
}
""";
        var bicepFilePath = FileHelper.SaveResultFile(TestContext, "main.bicep", bicepFileContents);
        var documentUri = DocumentUri.FromFileSystemPath(bicepFilePath);
        var uri = documentUri.ToUriEncoded();

        var files = new Dictionary<Uri, string>
        {
            [uri] = bicepFileContents,
        };

        var compilation = Services.BuildCompilation(files, uri);
        var diagnostics = compilation.GetEntrypointSemanticModel().GetAllDiagnostics();
        diagnostics.Should().ContainSingle(d => d.Code == "BCP057");

        var bcp057 = diagnostics.Single(d => d.Code == "BCP057");
        var diagnosticRange = bcp057.ToRange(compilation.SourceFileGrouping.EntryPoint.LineStarts);

        var helper = await ServerWithBuiltInTypes.GetAsync();
        await helper.OpenFileOnceAsync(TestContext, bicepFileContents, documentUri);

        var codeActions = await helper.Client.RequestCodeAction(new CodeActionParams
        {
            TextDocument = new TextDocumentIdentifier(documentUri),
            Range = diagnosticRange,
        });

        codeActions.Should().NotBeNull();
        var createParam = codeActions!.SingleOrDefault(x => x.CodeAction?.Title == $"Create parameter '{missingName}'");
        createParam.Should().NotBeNull();

        var bicepFile = new LanguageClientFile(documentUri, bicepFileContents);
        LspRefactoringHelper.ApplyCodeAction(bicepFile, createParam!.CodeAction!)
            .Should()
            .HaveSourceText("""
param sku object

resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
  name: 'st'
  location: 'westus'
  sku: sku
}
""");
    }
}

