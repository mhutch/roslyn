﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixesAndRefactorings;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CodeFixes;

/// <summary>
/// Provides a base class to write a <see cref="FixAllProvider"/> that fixes documents independently. This type
/// should be used instead of <see cref="WellKnownFixAllProviders.BatchFixer"/> in the case where fixes for a <see
/// cref="Diagnostic"/> only affect the <see cref="Document"/> the diagnostic was produced in.
/// </summary>
/// <remarks>
/// This type provides suitable logic for fixing large solutions in an efficient manner.  Projects are serially
/// processed, with all the documents in the project being processed in parallel.  Diagnostics are computed for the
/// project and then appropriately bucketed by document.  These are then passed to <see
/// cref="FixAllAsync(FixAllContext, Document, ImmutableArray{Diagnostic})"/> for implementors to process.
/// </remarks>
public abstract class DocumentBasedFixAllProvider : FixAllProvider
{
    private readonly ImmutableArray<FixAllScope> _supportedFixAllScopes;

    protected DocumentBasedFixAllProvider()
        : this(DefaultSupportedFixAllScopes)
    {
    }

    protected DocumentBasedFixAllProvider(ImmutableArray<FixAllScope> supportedFixAllScopes)
    {
        _supportedFixAllScopes = supportedFixAllScopes;
    }

    /// <summary>
    /// Produce a suitable title for the fix-all <see cref="CodeAction"/> this type creates in <see
    /// cref="GetFixAsync(FixAllContext)"/>.  Override this if customizing that title is desired.
    /// </summary>
    protected virtual string GetFixAllTitle(FixAllContext fixAllContext)
        => fixAllContext.GetDefaultFixAllTitle();

    /// <summary>
    /// Fix all the <paramref name="diagnostics"/> present in <paramref name="document"/>.  The document returned
    /// will only be examined for its content (e.g. it's <see cref="SyntaxTree"/> or <see cref="SourceText"/>.  No
    /// other aspects of (like it's properties), or changes to the <see cref="Project"/> or <see cref="Solution"/>
    /// it points at will be considered.
    /// </summary>
    /// <param name="fixAllContext">The context for the Fix All operation.</param>
    /// <param name="document">The document to fix.</param>
    /// <param name="diagnostics">The diagnostics to fix in the document.</param>
    /// <returns>
    /// <para>The new <see cref="Document"/> representing the content fixed document.</para>
    /// <para>-or-</para>
    /// <para><see langword="null"/>, if no changes were made to the document.</para>
    /// </returns>
    protected abstract Task<Document?> FixAllAsync(FixAllContext fixAllContext, Document document, ImmutableArray<Diagnostic> diagnostics);

    public sealed override IEnumerable<FixAllScope> GetSupportedFixAllScopes()
        => _supportedFixAllScopes;

    public sealed override Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        => DefaultFixAllProviderHelpers.GetFixAsync(
            fixAllContext.GetDefaultFixAllTitle(), fixAllContext, FixAllContextsHelperAsync);

    private Task<Solution?> FixAllContextsHelperAsync(FixAllContext originalFixAllContext, ImmutableArray<FixAllContext> fixAllContexts)
        => DocumentBasedFixAllProviderHelpers.FixAllContextsAsync(
            originalFixAllContext,
            fixAllContexts,
            originalFixAllContext.Progress,
            this.GetFixAllTitle(originalFixAllContext),
            DetermineDiagnosticsAndGetFixedDocumentsAsync);

    private async Task DetermineDiagnosticsAndGetFixedDocumentsAsync(
        FixAllContext fixAllContext, Action<(DocumentId documentId, (SyntaxNode? node, SourceText? text))> callback)
    {
        var cancellationToken = fixAllContext.CancellationToken;

        // First, determine the diagnostics to fix.
        var diagnostics = await FixAllContextHelper.GetDocumentDiagnosticsToFixAsync(fixAllContext).ConfigureAwait(false);

        // Second, get the fixes for all the diagnostics, and apply them to determine the new root/text for each doc.
        if (diagnostics.IsEmpty)
            return;

        // Then, process all documents in parallel to get the change for each doc.
        await RoslynParallel.ForEachAsync(
            source: diagnostics.Where(kvp => !kvp.Value.IsDefaultOrEmpty),
            cancellationToken,
            async (kvp, cancellationToken) =>
            {
                var (document, documentDiagnostics) = kvp;

                var newDocument = await this.FixAllAsync(fixAllContext, document, documentDiagnostics).ConfigureAwait(false);
                if (newDocument == null || newDocument == document)
                    return;

                // For documents that support syntax, grab the tree so that we can clean it up later.  If it's a
                // language that doesn't support that, then just grab the text.
                var node = newDocument.SupportsSyntaxTree ? await newDocument.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false) : null;
                var text = newDocument.SupportsSyntaxTree ? null : await newDocument.GetValueTextAsync(cancellationToken).ConfigureAwait(false);

                callback((document.Id, (node, text)));
            }).ConfigureAwait(false);
    }
}
