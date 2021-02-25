﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.Windows.CsWin32;
using Microsoft.Windows.CsWin32.Tests;
using Xunit;
using Xunit.Abstractions;

public class GeneratorTests : IDisposable, IAsyncLifetime
{
    private static readonly string FileSeparator = new string('=', 140);
    private readonly ITestOutputHelper logger;
    private readonly FileStream metadataStream;
    private CSharpCompilation compilation;
    private CSharpCompilation fastSpanCompilation;
    private CSharpParseOptions parseOptions;
    private Generator? generator;

    public GeneratorTests(ITestOutputHelper logger)
    {
        this.logger = logger;
        this.metadataStream = OpenMetadata();

        this.parseOptions = CSharpParseOptions.Default
            .WithDocumentationMode(DocumentationMode.Diagnose)
            .WithLanguageVersion(LanguageVersion.CSharp9);

        // set in InitializeAsync
        this.compilation = null!;
        this.fastSpanCompilation = null!;
    }

    public async Task InitializeAsync()
    {
        this.compilation = await this.CreateCompilationAsync(
            ReferenceAssemblies.NetStandard.NetStandard20
                .AddPackages(ImmutableArray.Create(new PackageIdentity("System.Memory", "4.5.4"))));

        this.fastSpanCompilation = await this.CreateCompilationAsync(
            ReferenceAssemblies.NetStandard.NetStandard21);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        this.generator?.Dispose();
        this.metadataStream.Dispose();
    }

    [Fact]
    public void SimplestMethod()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerateExternMethod("GetTickCount"));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
    }

    [Theory]
    [InlineData("CreateFile")] // SafeHandle-derived type
    [InlineData("D3DGetTraceInstructionOffsets")] // SizeParamIndex
    [InlineData("PlgBlt")] // SizeConst
    [InlineData("ID3D12Resource")] // COM interface with base types
    [InlineData("ID2D1RectangleGeometry")] // COM interface with base types
    [InlineData("ENABLE_TRACE_PARAMETERS_V1")] // bad xml created at some point.
    [InlineData("JsRuntimeVersion")] // An enum that has an extra member in a separate header file.
    [InlineData("ReportEvent")] // Failed at one point
    [InlineData("DISPLAYCONFIG_VIDEO_SIGNAL_INFO")] // Union, explicit layout, bitmask, nested structs
    [InlineData("g_wszStreamBufferRecordingDuration")] // Constant string field
    [InlineData("MFVideoAlphaBitmap")] // field named params
    [InlineData("DDRAWI_DDVIDEOPORT_INT")] // field that is never used
    [InlineData("MainAVIHeader")] // dwReserved field is a fixed length array
    [InlineData("JsRuntimeVersionEdge")] // Constant typed as an enum
    [InlineData("POSITIVE_INFINITY")] // Special float imaginary number
    [InlineData("NEGATIVE_INFINITY")] // Special float imaginary number
    [InlineData("NaN")] // Special float imaginary number
    [InlineData("HBMMENU_POPUP_RESTORE")] // A HBITMAP handle as a constant
    [InlineData("RpcServerRegisterIfEx")] // Optional attribute on delegate type.
    [InlineData("RpcSsSwapClientAllocFree")] // Parameters typed as pointers to in delegates and out delegates
    [InlineData("RPC_DISPATCH_TABLE")] // Struct with a field typed as a delegate
    [InlineData("RPC_SERVER_INTERFACE")] // Struct with a field typed as struct with a field typed as a delegate
    [InlineData("DDHAL_DESTROYDRIVERDATA")] // Struct with a field typed as a delegate
    [InlineData("I_RpcServerInqAddressChangeFn")] // p/invoke that returns a function pointer
    [InlineData("WSPUPCALLTABLE")] // a delegate with a delegate in its signature
    [InlineData("HWND_BOTTOM")] // A constant typed as a typedef'd struct
    [InlineData("BOOL")] // a special cased typedef struct
    [InlineData("uregex_getMatchCallback")] // friendly overload with delegate parameter, and out parameters
    [InlineData("CreateDispatcherQueueController")] // References a WinRT type
    [InlineData("RegOpenKey")] // allocates a handle with a release function that returns LSTATUS
    [InlineData("LsaRegisterLogonProcess")] // allocates a handle with a release function that returns NTSTATUS
    [InlineData("FilterCreate")] // allocates a handle with a release function that returns HRESULT
    [InlineData("DsGetDcOpen")] // allocates a handle with a release function that returns HRESULT
    [InlineData("DXVAHDSW_CALLBACKS")] // pointers to handles
    [InlineData("HBITMAP_UserMarshal")] // in+out handle pointer
    [InlineData("GetDiskFreeSpaceExW")] // ULARGE_INTEGER replaced with keyword: ulong.
    [InlineData("MsiGetProductPropertyW")] // MSIHANDLE (a 32-bit handle)
    public void InterestingAPIs(string api)
    {
        this.generator = new Generator(this.metadataStream, options: new GeneratorOptions { EmitSingleFile = true, WideCharOnly = false }, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate(api, CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
    }

    /// <summary>
    /// Verifies that GetLastError is never generated.
    /// Users should call <see cref="Marshal.GetLastWin32Error"/> instead.
    /// </summary>
    [Fact]
    public void GetLastErrorNotIncludedInBulkGeneration()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("kernel32.*", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        Assert.True(this.IsMethodGenerated("CreateFile"));
        Assert.False(this.IsMethodGenerated("GetLastError"));
    }

    [Fact]
    public void ReleaseMethodGeneratedWithHandleStruct()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("HANDLE", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        Assert.True(this.IsMethodGenerated("CloseHandle"));
    }

    [Fact]
    public void NamespaceHandleGetsNoSafeHandle()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("CreatePrivateNamespace", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        Assert.Empty(this.FindGeneratedType("ClosePrivateNamespaceSafeHandle"));
    }

    [Fact]
    public void CreateFileUsesSafeHandles()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("CreateFile", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();

        Assert.Contains(
            this.FindGeneratedMethod("CreateFile"),
            createFileMethod => createFileMethod!.ReturnType.ToString() == "Microsoft.Win32.SafeHandles.SafeFileHandle"
                && createFileMethod.ParameterList.Parameters.Last().Type?.ToString() == "SafeHandle");
    }

    [Fact]
    public void BOOL_ReturnTypeBecomes_Boolean()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("WinUsb_FlushPipe", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        MethodDeclarationSyntax? createFileMethod = this.FindGeneratedMethod("WinUsb_FlushPipe").FirstOrDefault();
        Assert.NotNull(createFileMethod);
        Assert.Equal(SyntaxKind.BoolKeyword, Assert.IsType<PredefinedTypeSyntax>(createFileMethod!.ReturnType).Keyword.Kind());
    }

    [Fact]
    public void NativeArray_SizeParamIndex_ProducesSimplerFriendlyOverload()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("EvtNext", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        IEnumerable<MethodDeclarationSyntax> overloads = this.FindGeneratedMethod("EvtNext");
        Assert.NotEmpty(overloads.Where(o => o.ParameterList.Parameters.Count == 5 && (o.ParameterList.Parameters[1].Type?.ToString().StartsWith("Span<", StringComparison.Ordinal) ?? false)));
    }

    [Fact]
    public void BOOL_ReturnTypeBecomes_Boolean_InCOMInterface()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("ISpellCheckerFactory", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        MethodDeclarationSyntax? method = this.FindGeneratedMethod("IsSupported").FirstOrDefault();
        Assert.NotNull(method);
        Assert.Equal(SyntaxKind.BoolKeyword, Assert.IsType<PredefinedTypeSyntax>(method!.ParameterList.Parameters.Last().Type).Keyword.Kind());
    }

    /// <summary>
    /// Verifies that fields are not converted from BOOL to bool.
    /// </summary>
    [Fact]
    public void BOOL_FieldRemainsBOOL()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("ICONINFO", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        var theStruct = (StructDeclarationSyntax)this.FindGeneratedType("ICONINFO").Single();
        Assert.Equal("BOOL", theStruct.Members.OfType<FieldDeclarationSyntax>().Select(m => m.Declaration).Single(d => d.Variables.Any(v => v.Identifier.ValueText == "fIcon")).Type.ToString());
    }

    [Fact]
    public void BSTR_FieldsDoNotBecomeSafeHandles()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("DebugPropertyInfo", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        StructDeclarationSyntax structDecl = Assert.IsType<StructDeclarationSyntax>(this.FindGeneratedType("DebugPropertyInfo").Single());
        var bstrField = structDecl.Members.OfType<FieldDeclarationSyntax>().First(m => m.Declaration.Variables.Any(v => v.Identifier.ValueText == "m_bstrName"));
        Assert.Equal("BSTR", ((IdentifierNameSyntax)bstrField.Declaration.Type).Identifier.ValueText);
    }

    /// <summary>
    /// Verifies that MSIHANDLE is wrapped with a SafeHandle even though it is a 32-bit handle.
    /// This is safe because we never pass SafeHandle directly to extern methods, so we can fix the length of the parameter or return value.
    /// </summary>
    [Fact]
    public void MSIHANDLE_BecomesSafeHandle()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("MsiGetLastErrorRecord", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();

        Assert.Contains(
            this.FindGeneratedMethod("MsiGetLastErrorRecord"),
            method => method!.ReturnType?.ToString() == "MSIHANDLE");

        Assert.Contains(
            this.FindGeneratedMethod("MsiGetLastErrorRecord_SafeHandle"),
            method => method!.ReturnType?.ToString() == "MsiCloseHandleSafeHandle");

        MethodDeclarationSyntax releaseMethod = this.FindGeneratedMethod("MsiCloseHandle").Single();
        Assert.Equal("MSIHANDLE", Assert.IsType<IdentifierNameSyntax>(releaseMethod!.ParameterList.Parameters[0].Type).Identifier.ValueText);
    }

    [Fact]
    public void OutHandleParameterBecomesSafeHandle()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        const string methodName = "TcAddFilter";
        Assert.True(this.generator.TryGenerate(methodName, CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();

        Assert.Contains(
            this.FindGeneratedMethod(methodName),
            method => method!.ParameterList.Parameters[2].Type?.ToString() == typeof(Microsoft.Win32.SafeHandles.SafeFileHandle).FullName);

        Assert.Contains(
            this.FindGeneratedMethod(methodName),
            method => method!.ParameterList.Parameters[(int)0].Type?.ToString() == nameof(SafeHandle));
    }

    [Fact]
    public void Const_PWSTR_Becomes_PCWSTR_and_String()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("StrCmpLogical", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();

        bool foundPCWSTROverload = false;
        bool foundStringOverload = false;
        IEnumerable<MethodDeclarationSyntax> overloads = this.FindGeneratedMethod("StrCmpLogical");
        foreach (MethodDeclarationSyntax method in overloads)
        {
            foundPCWSTROverload |= method!.ParameterList.Parameters[0].Type?.ToString() == "PCWSTR";
            foundStringOverload |= method!.ParameterList.Parameters[0].Type?.ToString() == "string";
        }

        Assert.True(foundPCWSTROverload, "PCWSTR overload is missing.");
        Assert.True(foundStringOverload, "string overload is missing.");
        Assert.Equal(2, overloads.Count());
    }

    [Theory]
    [InlineData("BOOL")]
    [InlineData("HRESULT")]
    [InlineData("MEMORY_BASIC_INFORMATION")]
    public void StructsArePartial(string structName)
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate(structName, CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        StructDeclarationSyntax structDecl = Assert.IsType<StructDeclarationSyntax>(this.FindGeneratedType(structName).Single());
        Assert.True(structDecl.Modifiers.Any(SyntaxKind.PartialKeyword));
    }

    [Fact]
    public void PartialStructsAllowUserContributions()
    {
        const string structName = "HRESULT";
        this.compilation = this.compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("namespace Microsoft.Windows.Sdk { partial struct HRESULT { void Foo() { } } }", this.parseOptions, "myHRESULT.cs"));

        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate(structName, CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();

        bool hasFooMethod = false;
        bool hasValueProperty = false;
        foreach (StructDeclarationSyntax structDecl in this.FindGeneratedType(structName))
        {
            hasFooMethod |= structDecl.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == "Foo");
            hasValueProperty |= structDecl.Members.OfType<FieldDeclarationSyntax>().Any(p => p.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText == "Value");
        }

        Assert.True(hasFooMethod, "User-defined method not found.");
        Assert.True(hasValueProperty, "Projected members not found.");
    }

    [Fact]
    public void GetLastErrorGenerationThrowsWhenExplicitlyCalled()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.Throws<NotSupportedException>(() => this.generator.TryGenerate("GetLastError", CancellationToken.None));
    }

    [Fact(Skip = "https://github.com/microsoft/win32metadata/issues/129")]
    public void DeleteObject_TakesTypeDefStruct()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("DeleteObject", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        MethodDeclarationSyntax? deleteObjectMethod = this.FindGeneratedMethod("DeleteObject").FirstOrDefault();
        Assert.NotNull(deleteObjectMethod);
        Assert.Equal("HGDIOBJ", Assert.IsType<IdentifierNameSyntax>(deleteObjectMethod!.ParameterList.Parameters[0].Type).Identifier.ValueText);
    }

    [Fact]
    public void CollidingStructNotGenerated()
    {
        const string test = @"
namespace Microsoft.Windows.Sdk
{
    internal enum FILE_CREATE_FLAGS
    {
        CREATE_NEW = 1,
        CREATE_ALWAYS = 2,
        OPEN_EXISTING = 3,
        OPEN_ALWAYS = 4,
        TRUNCATE_EXISTING = 5,
    }
}
";
        this.compilation = this.compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(test, path: "test.cs"));
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate("CreateFile", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
    }

    // Slow span can't safely create a span from a ref when the struct is on the heap.
    [Fact]
    public void CodeGenerationForFixedLengthInlineArrayWithSlowSpan()
    {
        const string expected = @"
    internal partial struct MainAVIHeader
    {
        internal uint dwMicroSecPerFrame;
        internal uint dwMaxBytesPerSec;
        internal uint dwPaddingGranularity;
        internal uint dwFlags;
        internal uint dwTotalFrames;
        internal uint dwInitialFrames;
        internal uint dwStreams;
        internal uint dwSuggestedBufferSize;
        internal uint dwWidth;
        internal uint dwHeight;
        internal __dwReserved_4 dwReserved;
        internal struct __dwReserved_4
        {
            internal uint _1, _2, _3, _4;
            internal ref uint this[int index]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    unsafe
                    {
                        fixed (uint *p = &_1)
                            return ref p[index];
                    }
                }
            }
        }
    }
";

        this.AssertGeneratedType("MainAVIHeader", expected);
    }

    [Fact]
    public void CodeGenerationForFixedLengthInlineArrayWithFastSpan()
    {
        const string expected = @"
    internal partial struct MainAVIHeader
    {
        internal uint dwMicroSecPerFrame;
        internal uint dwMaxBytesPerSec;
        internal uint dwPaddingGranularity;
        internal uint dwFlags;
        internal uint dwTotalFrames;
        internal uint dwInitialFrames;
        internal uint dwStreams;
        internal uint dwSuggestedBufferSize;
        internal uint dwWidth;
        internal uint dwHeight;
        internal __dwReserved_4 dwReserved;
        internal struct __dwReserved_4
        {
            internal uint _1, _2, _3, _4;
            internal ref uint this[int index] => ref AsSpan()[index];
            internal Span<uint> AsSpan() => MemoryMarshal.CreateSpan(ref _1, 4);
        }
    }
";

        this.compilation = this.fastSpanCompilation;
        this.AssertGeneratedType("MainAVIHeader", expected);
    }

    [Fact]
    public void FullGeneration()
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        this.generator.GenerateAll(CancellationToken.None);
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics(logGeneratedCode: false);
    }

    [Theory, PairwiseData]
    public void ProjectReferenceBetweenTwoGeneratingProjects(bool internalsVisibleTo)
    {
        CSharpCompilation referencedProject = this.compilation
            .WithAssemblyName("refdProj");
        if (internalsVisibleTo)
        {
            referencedProject = referencedProject.AddSyntaxTrees(
                CSharpSyntaxTree.ParseText($@"[assembly: System.Runtime.CompilerServices.InternalsVisibleToAttribute(""{this.compilation.AssemblyName}"")]", this.parseOptions));
        }

        using var referencedGenerator = new Generator(OpenMetadata(), new GeneratorOptions { ClassName = "P1" }, referencedProject, this.parseOptions);
        Assert.True(referencedGenerator.TryGenerate("LockWorkStation", CancellationToken.None));
        Assert.True(referencedGenerator.TryGenerate("CreateFile", CancellationToken.None));
        referencedProject = this.AddGeneratedCode(referencedProject, referencedGenerator);
        this.AssertNoDiagnostics(referencedProject);

        // Now produce more code in a referencing project that includes at least one of the same types as generated in the referenced project.
        this.compilation = this.compilation.AddReferences(referencedProject.ToMetadataReference());
        this.generator = new Generator(this.metadataStream, new GeneratorOptions { ClassName = "P2" }, this.compilation, this.parseOptions);
        Assert.True(this.generator.TryGenerate("HidD_GetAttributes", CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
    }

    private static ImmutableArray<Diagnostic> FilterDiagnostics(ImmutableArray<Diagnostic> diagnostics) => diagnostics.Where(d => d.Severity > DiagnosticSeverity.Hidden).ToImmutableArray();

    private static FileStream OpenMetadata()
    {
        return File.OpenRead(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location!)!, "Windows.Win32.winmd"));
    }

    private CSharpCompilation AddGeneratedCode(CSharpCompilation compilation, Generator generator)
    {
        var compilationUnits = generator.GetCompilationUnits(CancellationToken.None);
        var syntaxTrees = new List<SyntaxTree>(compilationUnits.Count);
        foreach (var unit in compilationUnits)
        {
            // Our syntax trees aren't quite right. And anyway the source generator API only takes text anyway so it doesn't _really_ matter.
            // So render the trees as text and have C# re-parse them so we get the same compiler warnings/errors that the user would get.
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(unit.Value.ToFullString(), this.parseOptions, path: unit.Key));
        }

        return compilation.AddSyntaxTrees(syntaxTrees);
    }

    private void CollectGeneratedCode(Generator generator) => this.compilation = this.AddGeneratedCode(this.compilation, generator);

    private IEnumerable<MethodDeclarationSyntax> FindGeneratedMethod(string name) => this.compilation.SyntaxTrees.SelectMany(st => st.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()).Where(md => md.Identifier.ValueText == name);

    private IEnumerable<BaseTypeDeclarationSyntax> FindGeneratedType(string name) => this.compilation.SyntaxTrees.SelectMany(st => st.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()).Where(md => md.Identifier.ValueText == name);

    private bool IsMethodGenerated(string name) => this.FindGeneratedMethod(name).Any();

    private void AssertNoDiagnostics(bool logGeneratedCode = true) => this.AssertNoDiagnostics(this.compilation, logGeneratedCode);

    private void AssertNoDiagnostics(CSharpCompilation compilation, bool logGeneratedCode = true)
    {
        var diagnostics = FilterDiagnostics(compilation.GetDiagnostics());
        this.LogDiagnostics(diagnostics);

        var emitDiagnostics = ImmutableArray<Diagnostic>.Empty;
        bool? emitSuccessful = null;
        if (diagnostics.IsEmpty)
        {
            var emitResult = compilation.Emit(peStream: Stream.Null, xmlDocumentationStream: Stream.Null);
            emitSuccessful = emitResult.Success;
            emitDiagnostics = FilterDiagnostics(emitResult.Diagnostics);
            this.LogDiagnostics(emitDiagnostics);
        }

        if (logGeneratedCode)
        {
            this.LogGeneratedCode(compilation);
        }

        Assert.Empty(diagnostics);
        if (emitSuccessful.HasValue)
        {
            Assert.Empty(emitDiagnostics);
            Assert.True(emitSuccessful);
        }
    }

    private void LogDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            this.logger.WriteLine(diagnostic.ToString());
        }
    }

    private void LogGeneratedCode(CSharpCompilation compilation)
    {
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            this.logger.WriteLine(FileSeparator);
            this.logger.WriteLine($"{tree.FilePath} content:");
            this.logger.WriteLine(FileSeparator);
            using var lineWriter = new NumberedLineWriter(this.logger);
            tree.GetRoot().WriteTo(lineWriter);
            lineWriter.WriteLine(string.Empty);
        }
    }

    private void AssertGeneratedType(string apiName, string expectedSyntax)
    {
        this.generator = new Generator(this.metadataStream, compilation: this.compilation, parseOptions: this.parseOptions);
        Assert.True(this.generator.TryGenerate(apiName, CancellationToken.None));
        this.CollectGeneratedCode(this.generator);
        this.AssertNoDiagnostics();
        BaseTypeDeclarationSyntax? syntax = Assert.Single(this.FindGeneratedType(apiName));
        Assert.Equal(TestUtils.NormalizeToExpectedLineEndings(expectedSyntax), syntax?.ToFullString());
    }

    private async Task<CSharpCompilation> CreateCompilationAsync(ReferenceAssemblies references)
    {
        ImmutableArray<MetadataReference> metadataReferences = await references
            .AddPackages(ImmutableArray.Create(new PackageIdentity("Microsoft.Windows.SDK.Contracts", "10.0.19041.1")))
            .ResolveAsync(LanguageNames.CSharp, default);

        // Workaround for https://github.com/dotnet/roslyn-sdk/issues/699
        metadataReferences = metadataReferences.AddRange(
            Directory.GetFiles(Path.Combine(Path.GetTempPath(), "test-packages", "Microsoft.Windows.SDK.Contracts.10.0.19041.1", "ref", "netstandard2.0"), "*.winmd").Select(p => MetadataReference.CreateFromFile(p)));

        // CONSIDER: How can I pass in the source generator itself, with AdditionalFiles, so I'm exercising that code too?
        var compilation = CSharpCompilation.Create(
            assemblyName: "test",
            references: metadataReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // Add a namespace that WinUI projects define to ensure we prefix types with "global::" everywhere.
        compilation = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("namespace Microsoft.System { }", this.parseOptions, path: "Microsoft.System.cs"));

        return compilation;
    }
}
