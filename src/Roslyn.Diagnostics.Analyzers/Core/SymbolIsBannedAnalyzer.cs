﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Analyzer.Utilities;
using Analyzer.Utilities.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Roslyn.Diagnostics.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public sealed class SymbolIsBannedAnalyzer : DiagnosticAnalyzer
    {
        internal const string BannedSymbolsFileName = "BannedSymbols.txt";

        internal static readonly DiagnosticDescriptor SymbolIsBannedRule = new DiagnosticDescriptor(
            id: RoslynDiagnosticIds.SymbolIsBannedRuleId,
            title: RoslynDiagnosticsAnalyzersResources.SymbolIsBannedTitle,
            messageFormat: RoslynDiagnosticsAnalyzersResources.SymbolIsBannedMessage,
            category: "ApiDesign",
            defaultSeverity: DiagnosticHelpers.DefaultDiagnosticSeverity,
            isEnabledByDefault: DiagnosticHelpers.EnabledByDefaultIfNotBuildingVSIX,
            description: RoslynDiagnosticsAnalyzersResources.SymbolIsBannedDescription,
            customTags: WellKnownDiagnosticTags.Telemetry);

        internal static readonly DiagnosticDescriptor DuplicateBannedSymbolRule = new DiagnosticDescriptor(
            id: RoslynDiagnosticIds.DuplicateBannedSymbolRuleId,
            title: RoslynDiagnosticsAnalyzersResources.DuplicateBannedSymbolTitle,
            messageFormat: RoslynDiagnosticsAnalyzersResources.DuplicateBannedSymbolMessage,
            category: "ApiDesign",
            defaultSeverity: DiagnosticHelpers.DefaultDiagnosticSeverity,
            isEnabledByDefault: DiagnosticHelpers.EnabledByDefaultIfNotBuildingVSIX,
            description: RoslynDiagnosticsAnalyzersResources.DuplicateBannedSymbolDescription,
            customTags: WellKnownDiagnosticTags.Telemetry);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(SymbolIsBannedRule, DuplicateBannedSymbolRule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();

            // Analyzer needs to get callbacks for generated code, and might report diagnostics in generated code.
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext compilationContext)
        {
            var bannedSymbols = ReadBannedApis(compilationContext);

            if (bannedSymbols.Count > 0)
            {
                var symbolDisplayFormat = compilationContext.Compilation.Language == LanguageNames.CSharp
                    ? SymbolDisplayFormat.CSharpShortErrorMessageFormat
                    : SymbolDisplayFormat.VisualBasicShortErrorMessageFormat;

                var bannedAttributes = bannedSymbols
                    .Where(s => s is ITypeSymbol n && n.IsAttribute())
                    .ToImmutableHashSet();

                if (bannedAttributes.Count > 0)
                {
                    compilationContext.RegisterCompilationEndAction(context =>
                    {
                        VerifyAttributes(context.ReportDiagnostic, compilationContext.Compilation.Assembly.GetAttributes());
                        VerifyAttributes(context.ReportDiagnostic, compilationContext.Compilation.SourceModule.GetAttributes());
                    });

                    compilationContext.RegisterSymbolAction(
                        sac => VerifyAttributes(sac.ReportDiagnostic, sac.Symbol.GetAttributes()),
                        SymbolKind.NamedType,
                        SymbolKind.Method,
                        SymbolKind.Field,
                        SymbolKind.Property,
                        SymbolKind.Event);
                }

                compilationContext.RegisterOperationAction(
                    oac => AnalyzeOperation(oac, bannedSymbols, symbolDisplayFormat),
                    OperationKind.ObjectCreation,
                    OperationKind.Invocation,
                    OperationKind.EventReference,
                    OperationKind.FieldReference,
                    OperationKind.MethodReference,
                    OperationKind.PropertyReference);

                void VerifyAttributes(Action<Diagnostic> reportDiagnostic, ImmutableArray<AttributeData> attributes)
                {
                    foreach (AttributeData attribute in attributes)
                    {
                        if (bannedAttributes.Contains(attribute.AttributeClass))
                        {
                            SyntaxNode node = attribute.ApplicationSyntaxReference.GetSyntax();
                            reportDiagnostic(node.CreateDiagnostic(SymbolIsBannedRule, attribute.AttributeClass.ToDisplayString()));
                        }
                    }
                }
            }
        }

        private static ImmutableHashSet<ISymbol> ReadBannedApis(CompilationStartAnalysisContext context)
        {
            var query = 
                from additionalFile in context.Options.AdditionalFiles
                where StringComparer.Ordinal.Equals(Path.GetFileName(additionalFile.Path), BannedSymbolsFileName)
                let sourceText = additionalFile.GetText(context.CancellationToken)
                where sourceText != null
                from line in sourceText.Lines
                let text = line.ToString()
                where !string.IsNullOrWhiteSpace(text)
                select new ApiLine(text, line.Span, sourceText, additionalFile.Path);

            var apiLines = query.ToList();

            if (apiLines.Count == 0)
            {
                return ImmutableHashSet<ISymbol>.Empty;
            }

            var lineById = new Dictionary<string, ApiLine>(StringComparer.Ordinal);
            var errors = new List<Diagnostic>();
            var bannedSymbols = ImmutableHashSet.CreateBuilder<ISymbol>();

            foreach (var line in apiLines)
            {
                if (lineById.TryGetValue(line.Text, out ApiLine existingLine))
                {
                    errors.Add(Diagnostic.Create(DuplicateBannedSymbolRule, line.Location, new[] { existingLine.Location }, line.Text));
                    continue;
                }

                lineById.Add(line.Text, line);

                var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(line.Text, context.Compilation);
                if (!symbols.IsDefaultOrEmpty)
                {
                    bannedSymbols.UnionWith(symbols);
                }
            }

            if (errors.Count != 0)
            {
                context.RegisterCompilationEndAction(
                    endContext =>
                    {
                        foreach (var error in errors)
                        {
                            endContext.ReportDiagnostic(error);
                        }
                    });
            }

            return bannedSymbols.ToImmutable();
        }

        private static void AnalyzeOperation(OperationAnalysisContext oac, ImmutableHashSet<ISymbol> bannedSymbols, SymbolDisplayFormat symbolDisplayFormat)
        {
            ITypeSymbol type = null;
            switch (oac.Operation)
            {
                case IObjectCreationOperation objectCreation:
                    type = objectCreation.Type.OriginalDefinition;
                    break;

                case IInvocationOperation invocation:
                    type = invocation.TargetMethod.ContainingType.OriginalDefinition;
                    break;

                case IMemberReferenceOperation memberReference:
                    type = memberReference.Member.ContainingType.OriginalDefinition;
                    break;
            }

            while (!(type is null))
            {
                if (bannedSymbols.Contains(type))
                {
                    oac.ReportDiagnostic(Diagnostic.Create(SymbolIsBannedRule, oac.Operation.Syntax.GetLocation(), type.ToDisplayString(symbolDisplayFormat)));
                    break;
                }

                type = type.ContainingType;
            }
        }

        private sealed class ApiLine
        {
            public TextSpan Span { get; }
            public SourceText SourceText { get; }
            public string Path { get; }
            public string Text { get; }

            public ApiLine(string text, TextSpan span, SourceText sourceText, string path)
            {
                Text = text;
                Span = span;
                SourceText = sourceText;
                Path = path;
            }

            public Location Location => Location.Create(Path, Span, SourceText.Lines.GetLinePositionSpan(Span));
        }
    }
}
