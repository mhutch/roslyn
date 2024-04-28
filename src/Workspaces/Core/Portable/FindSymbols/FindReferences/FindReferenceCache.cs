﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.FindSymbols;

/// <summary>
/// Caches information find-references needs associated with each document.  Computed and cached so that multiple calls
/// to find-references in a row can share the same data.
/// </summary>
internal sealed class FindReferenceCache
{
    private static readonly ConditionalWeakTable<Document, AsyncLazy<FindReferenceCache>> s_cache = new();

    public static async ValueTask<FindReferenceCache> GetCacheAsync(Document document, CancellationToken cancellationToken)
    {
        var lazy = s_cache.GetValue(document, static document => AsyncLazy.Create(ComputeCacheAsync, document));
        return await lazy.GetValueAsync(cancellationToken).ConfigureAwait(false);

        static async Task<FindReferenceCache> ComputeCacheAsync(Document document, CancellationToken cancellationToken)
        {
            var text = await document.GetValueTextAsync(cancellationToken).ConfigureAwait(false);

            // Find-Refs is not impacted by nullable types at all.  So get a nullable-disabled semantic model to avoid
            // unnecessary costs while binding.
            var model = await document.GetRequiredNullableDisabledSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            // It's very costly to walk an entire tree.  So if the tree is simple and doesn't contain
            // any unicode escapes in it, then we do simple string matching to find the tokens.
            var index = await SyntaxTreeIndex.GetRequiredIndexAsync(document, cancellationToken).ConfigureAwait(false);

            return new(document, text, model, root, index);
        }
    }

    public readonly Document Document;
    public readonly SourceText Text;
    public readonly SemanticModel SemanticModel;
    public readonly SyntaxNode Root;
    public readonly ISyntaxFactsService SyntaxFacts;
    public readonly SyntaxTreeIndex SyntaxTreeIndex;

    private readonly ConcurrentDictionary<SyntaxNode, SymbolInfo> _symbolInfoCache = [];
    private readonly ConcurrentDictionary<string, ImmutableArray<SyntaxToken>> _identifierCache;

    private ImmutableHashSet<string>? _aliasNameSet;
    private ImmutableArray<SyntaxToken> _constructorInitializerCache;

    private FindReferenceCache(
        Document document, SourceText text, SemanticModel semanticModel, SyntaxNode root, SyntaxTreeIndex syntaxTreeIndex)
    {
        Document = document;
        Text = text;
        SemanticModel = semanticModel;
        Root = root;
        SyntaxTreeIndex = syntaxTreeIndex;
        SyntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();

        _identifierCache = new(comparer: semanticModel.Language switch
        {
            LanguageNames.VisualBasic => StringComparer.OrdinalIgnoreCase,
            LanguageNames.CSharp => StringComparer.Ordinal,
            _ => throw ExceptionUtilities.UnexpectedValue(semanticModel.Language)
        });
    }

    public SymbolInfo GetSymbolInfo(SyntaxNode node, CancellationToken cancellationToken)
        => _symbolInfoCache.GetOrAdd(node, static (n, arg) => arg.SemanticModel.GetSymbolInfo(n, arg.cancellationToken), (SemanticModel, cancellationToken));

    public IAliasSymbol? GetAliasInfo(
        ISemanticFactsService semanticFacts, SyntaxToken token, CancellationToken cancellationToken)
    {
        if (_aliasNameSet == null)
        {
            var set = semanticFacts.GetAliasNameSet(SemanticModel, cancellationToken);
            Interlocked.CompareExchange(ref _aliasNameSet, set, null);
        }

        if (_aliasNameSet.Contains(token.ValueText))
            return SemanticModel.GetAliasInfo(token.GetRequiredParent(), cancellationToken);

        return null;
    }

    public ImmutableArray<SyntaxToken> FindMatchingIdentifierTokens(
        string identifier, CancellationToken cancellationToken)
    {
        if (identifier == "")
        {
            // Certain symbols don't have a name, so we return without further searching since the text-based index
            // and lookup never terminates if searching for an empty string.
            // https://devdiv.visualstudio.com/DevDiv/_workitems/edit/1655431
            // https://devdiv.visualstudio.com/DevDiv/_workitems/edit/1744118
            // https://devdiv.visualstudio.com/DevDiv/_workitems/edit/1820930
            return [];
        }

        if (_identifierCache.TryGetValue(identifier, out var result))
            return result;

        // If this document doesn't even contain this identifier (escaped or non-escaped) we don't have to search it at all.
        if (!this.SyntaxTreeIndex.ProbablyContainsIdentifier(identifier))
            return [];

        // If the identifier was escaped in the file then we'll have to do a more involved search that actually
        // walks the root and checks all identifier tokens.
        //
        // otherwise, we can use the text of the document to quickly find candidates and test those directly.
        return this.SyntaxTreeIndex.ProbablyContainsEscapedIdentifier(identifier)
            ? _identifierCache.GetOrAdd(identifier, identifier => FindMatchingIdentifierTokensFromTree(identifier, cancellationToken))
            : _identifierCache.GetOrAdd(identifier, _ => FindMatchingIdentifierTokensFromText(identifier, cancellationToken));
    }

    private bool IsMatch(string identifier, SyntaxToken token)
        => !token.IsMissing && this.SyntaxFacts.IsIdentifier(token) && this.SyntaxFacts.TextMatch(token.ValueText, identifier);

    private ImmutableArray<SyntaxToken> FindMatchingIdentifierTokensFromTree(
        string identifier, CancellationToken cancellationToken)
    {
        using var _ = ArrayBuilder<SyntaxToken>.GetInstance(out var result);
        using var obj = SharedPools.Default<Stack<SyntaxNodeOrToken>>().GetPooledObject();

        var stack = obj.Object;
        stack.Push(this.Root);

        while (stack.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.IsNode)
            {
                foreach (var child in current.AsNode()!.ChildNodesAndTokens().Reverse())
                    stack.Push(child);
            }
            else if (current.IsToken)
            {
                var token = current.AsToken();
                if (IsMatch(identifier, token))
                    result.Add(token);

                if (token.HasStructuredTrivia)
                {
                    // structured trivia can only be leading trivia
                    foreach (var trivia in token.LeadingTrivia)
                    {
                        if (trivia.HasStructure)
                            stack.Push(trivia.GetStructure()!);
                    }
                }
            }
        }

        return result.ToImmutableAndClear();
    }

    private ImmutableArray<SyntaxToken> FindMatchingIdentifierTokensFromText(
        string identifier, CancellationToken cancellationToken)
    {
        using var _ = ArrayBuilder<SyntaxToken>.GetInstance(out var result);

        var index = 0;
        while ((index = this.Text.IndexOf(identifier, index, this.SyntaxFacts.IsCaseSensitive)) >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var token = this.Root.FindToken(index, findInsideTrivia: true);
            var span = token.Span;
            if (span.Start == index && span.Length == identifier.Length && IsMatch(identifier, token))
                result.Add(token);

            var nextIndex = index + identifier.Length;
            nextIndex = Math.Max(nextIndex, token.SpanStart);
            index = nextIndex;
        }

        return result.ToImmutableAndClear();
    }
    public IEnumerable<SyntaxToken> GetConstructorInitializerTokens(
        ISyntaxFactsService syntaxFacts, SyntaxNode root, CancellationToken cancellationToken)
    {
        // this one will only get called when we know given document contains constructor initializer.
        // no reason to use text to check whether it exist first.
        if (_constructorInitializerCache.IsDefault)
        {
            var initializers = GetConstructorInitializerTokensWorker(syntaxFacts, root, cancellationToken);
            ImmutableInterlocked.InterlockedInitialize(ref _constructorInitializerCache, initializers);
        }

        return _constructorInitializerCache;
    }

    private static ImmutableArray<SyntaxToken> GetConstructorInitializerTokensWorker(
        ISyntaxFactsService syntaxFacts, SyntaxNode root, CancellationToken cancellationToken)
    {
        using var _ = ArrayBuilder<SyntaxToken>.GetInstance(out var initializers);
        foreach (var constructor in syntaxFacts.GetConstructors(root, cancellationToken))
        {
            foreach (var token in constructor.DescendantTokens(descendIntoTrivia: false))
            {
                if (syntaxFacts.IsThisConstructorInitializer(token) || syntaxFacts.IsBaseConstructorInitializer(token))
                    initializers.Add(token);
            }
        }

        return initializers.ToImmutableAndClear();
    }
}
