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
    private async Task<string> ApplyUndefinedSymbolCodeFix(string bicepFileContents, string missingName, string actionType)
    {
        var bicepFilePath = FileHelper.SaveResultFile(TestContext, "main.bicep", bicepFileContents);
        var documentUri = DocumentUri.FromFileSystemPath(bicepFilePath);
        var uri = documentUri.ToUriEncoded();

        var files = new Dictionary<Uri, string> { [uri] = bicepFileContents };

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
        var expectedTitle = actionType == "parameter"
            ? $"Create parameter '{missingName}'"
            : $"Create variable '{missingName}'";
        var codeAction = codeActions!.SingleOrDefault(x => x.CodeAction?.Title == expectedTitle);
        codeAction.Should().NotBeNull($"Expected to find '{expectedTitle}' code action");

        var bicepFile = new LanguageClientFile(documentUri, bicepFileContents);
        return LspRefactoringHelper.ApplyCodeAction(bicepFile, codeAction!.CodeAction!).Text;
    }

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
        var result = await ApplyUndefinedSymbolCodeFix("""
            resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
              name: 'st'
              location: 'westus'
              publicNetworkAccess: enablePrivateEndpoint ? 'Disabled' : 'Enabled'
            }
            """, "enablePrivateEndpoint", "parameter");

        result.Should().Be("""
            param enablePrivateEndpoint bool

            resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
              name: 'st'
              location: 'westus'
              publicNetworkAccess: enablePrivateEndpoint ? 'Disabled' : 'Enabled'
            }
            """);
    }

    [TestMethod]
    public async Task Undefined_name_should_offer_create_variable_with_bool_initializer()
    {
        var result = await ApplyUndefinedSymbolCodeFix("""
            resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
              name: 'st'
              location: 'westus'
              publicNetworkAccess: enablePrivateEndpoint ? 'Disabled' : 'Enabled'
            }
            """, "enablePrivateEndpoint", "variable");

        result.Should().Be("""
            var enablePrivateEndpoint = false

            resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
              name: 'st'
              location: 'westus'
              publicNetworkAccess: enablePrivateEndpoint ? 'Disabled' : 'Enabled'
            }
            """);
    }

    [TestMethod]
    public async Task Undefined_name_should_offer_create_variable_with_int_initializer()
    {
        var result = await ApplyUndefinedSymbolCodeFix("""
            output total int = replicas + 2
            """, "replicas", "variable");

        result.Should().Be("""
            var replicas = 0

            output total int = replicas + 2
            """);
    }

    [TestMethod]
    public async Task Undefined_name_should_offer_create_variable_with_object_initializer()
    {
        var result = await ApplyUndefinedSymbolCodeFix("""
            resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
              name: 'st'
              location: 'westus'
              sku: sku
            }
            """, "sku", "variable");

        result.Should().Be("""
            var sku = {}

            resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
              name: 'st'
              location: 'westus'
              sku: sku
            }
            """);
    }

    [TestMethod]
    public async Task Undefined_name_should_offer_create_variable_with_typed_object_properties()
    {
        var result = await ApplyUndefinedSymbolCodeFix("""
            type ConfigType = {
              enabled: bool
              count: int
              name: string
            }

            output out ConfigType = config
            """, "config", "variable");

        result.Should().Be("""
            type ConfigType = {
              enabled: bool
              count: int
              name: string
            }

            var config = { count: 0, enabled: false, name: '' }

            output out ConfigType = config
            """);
    }

    [TestMethod]
    public async Task Undefined_name_should_offer_create_variable_with_union_type_initializer()
    {
        var result = await ApplyUndefinedSymbolCodeFix("""
            type StorageSkuType = 'Standard_LRS' | 'Standard_GRS' | 'Premium_LRS'

            output sku StorageSkuType = storageType
            """, "storageType", "variable");

        result.Should().Be("""
            type StorageSkuType = 'Standard_LRS' | 'Standard_GRS' | 'Premium_LRS'

            var storageType = 'Premium_LRS'

            output sku StorageSkuType = storageType
            """);
    }

    // TODO: Named type support needs more work to properly track type aliases
    // [TestMethod]
    // public async Task Undefined_name_should_offer_create_parameter_with_named_type()

    [TestMethod]
    public async Task Undefined_name_should_offer_create_parameter_with_resource_derived_type()
    {
        const string missingName = "storagesku";
        var bicepFileContents = """
param storageAccountName string
param location string

resource st 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: storagesku
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
param storageAccountName string
param location string
param storagesku resourceInput<'Microsoft.Storage/storageAccounts@2023-01-01'>.sku


resource st 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: storagesku
}
""");
    }

    [TestMethod]
    public async Task Undefined_name_should_offer_create_variable_with_array_initializer()
    {
        var result = await ApplyUndefinedSymbolCodeFix("""
            output out array = myItems
            """, "myItems", "variable");

        result.Should().Be("""
            var myItems = []

            output out array = myItems
            """);
    }

    [TestMethod]
    public async Task Undefined_name_used_in_arithmetic_should_infer_int_parameter()
    {
        var result = await ApplyUndefinedSymbolCodeFix("""
            output total int = replicas + 2
            """, "replicas", "parameter");

        result.Should().Be("""
            param replicas int

            output total int = replicas + 2
            """);
    }

    [TestMethod]
    public async Task Undefined_name_used_in_object_context_should_infer_object_parameter()
    {
        var result = await ApplyUndefinedSymbolCodeFix("""
            resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
              name: 'st'
              location: 'westus'
              sku: sku
            }
            """, "sku", "parameter");

        result.Should().Be("""
            param sku resourceInput<'Microsoft.Storage/storageAccounts@2022-09-01'>.sku

            resource st 'Microsoft.Storage/storageAccounts@2022-09-01' = {
              name: 'st'
              location: 'westus'
              sku: sku
            }
            """);
    }

    [TestMethod]
    public async Task Undefined_name_used_in_resource_properties_should_infer_resourceInput_parameter()
    {
        var result = await ApplyUndefinedSymbolCodeFix("""
            resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
              name: 'mystorageacct123'
              location: resourceGroup().location
              sku: {
                name: 'Standard_LRS'
              }
              kind: 'StorageV2'
              properties: storageAccountProps
            }
            """, "storageAccountProps", "parameter");

        result.Should().Be("""
            param storageAccountProps resourceInput<'Microsoft.Storage/storageAccounts@2023-01-01'>.properties

            resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
              name: 'mystorageacct123'
              location: resourceGroup().location
              sku: {
                name: 'Standard_LRS'
              }
              kind: 'StorageV2'
              properties: storageAccountProps
            }
            """);
    }

    [TestMethod]
    public async Task Undefined_name_used_in_nested_resource_properties_should_infer_resourceInput_parameter()
    {
        var result = await ApplyUndefinedSymbolCodeFix("""
            resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
              name: 'mystorageacct123'
              location: resourceGroup().location
              sku: {
                name: 'Standard_LRS'
              }
              kind: 'StorageV2'
              properties: {
                minimumTlsVersion: 'TLS1_2'
                allowBlobPublicAccess: false
                supportsHttpsTrafficOnly: true
                encryption: enc
              }
            }
            """, "enc", "parameter");

        result.Should().Be("""
            param enc resourceInput<'Microsoft.Storage/storageAccounts@2023-01-01'>.properties.encryption

            resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
              name: 'mystorageacct123'
              location: resourceGroup().location
              sku: {
                name: 'Standard_LRS'
              }
              kind: 'StorageV2'
              properties: {
                minimumTlsVersion: 'TLS1_2'
                allowBlobPublicAccess: false
                supportsHttpsTrafficOnly: true
                encryption: enc
              }
            }
            """);
    }
}

