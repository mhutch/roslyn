﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.VisualStudio.GraphModel;
using Microsoft.VisualStudio.Progression;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.Progression
{
    using Workspace = Microsoft.CodeAnalysis.Workspace;

    internal class GraphQueryManager
    {
        private readonly Workspace _workspace;
        private readonly IAsynchronousOperationListener _asyncListener;

        /// <summary>
        /// This gate locks manipulation of <see cref="_trackedQueries"/>.
        /// </summary>
        private readonly object _gate = new();
        private readonly List<(WeakReference<IGraphContext>, ImmutableArray<IGraphQuery>)> _trackedQueries = new();

        // We update all of our tracked queries when this delay elapses.
        private ResettableDelay? _delay;

        internal GraphQueryManager(Workspace workspace, IAsynchronousOperationListener asyncListener)
        {
            _workspace = workspace;
            _asyncListener = asyncListener;
        }

        public void AddQueries(IGraphContext context, ImmutableArray<IGraphQuery> graphQueries)
        {
            var asyncToken = _asyncListener.BeginAsyncOperation("GraphQueryManager.AddQueries");

            var solution = _workspace.CurrentSolution;

            var populateTask = PopulateContextGraphAsync(solution, graphQueries, context);

            // We want to ensure that no matter what happens, this initial context is completed
            var task = populateTask.SafeContinueWith(
                _ => context.OnCompleted(), context.CancelToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            if (context.TrackChanges)
            {
                task = task.SafeContinueWith(
                    _ => TrackChangesAfterFirstPopulate(graphQueries, context, solution),
                    context.CancelToken, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            task.CompletesAsyncOperation(asyncToken);
        }

        private void TrackChangesAfterFirstPopulate(ImmutableArray<IGraphQuery> graphQueries, IGraphContext context, Solution solution)
        {
            var workspace = solution.Workspace;
            var contextWeakReference = new WeakReference<IGraphContext>(context);

            lock (_gate)
            {
                if (_trackedQueries.IsEmpty())
                {
                    _workspace.WorkspaceChanged += OnWorkspaceChanged;
                }

                _trackedQueries.Add(ValueTuple.Create(contextWeakReference, graphQueries));
            }

            EnqueueUpdateIfSolutionIsStale(solution);
        }

        private void EnqueueUpdateIfSolutionIsStale(Solution solution)
        {
            // It's possible the workspace changed during our initial population, so let's enqueue an update if it did
            if (_workspace.CurrentSolution != solution)
            {
                EnqueueUpdate();
            }
        }

        private void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
            => EnqueueUpdate();

        private void EnqueueUpdate()
        {
            const int WorkspaceUpdateDelay = 1500;
            var delay = _delay;
            if (delay == null)
            {
                var newDelay = new ResettableDelay(WorkspaceUpdateDelay, _asyncListener);
                if (Interlocked.CompareExchange(ref _delay, newDelay, null) == null)
                {
                    var asyncToken = _asyncListener.BeginAsyncOperation("WorkspaceGraphQueryManager.EnqueueUpdate");
                    newDelay.Task.SafeContinueWithFromAsync(_ => UpdateAsync(), CancellationToken.None, TaskScheduler.Default)
                        .CompletesAsyncOperation(asyncToken);
                }

                return;
            }

            delay.Reset();
        }

        private Task UpdateAsync()
        {
            List<ValueTuple<IGraphContext, ImmutableArray<IGraphQuery>>> liveQueries;
            lock (_gate)
            {
                liveQueries = _trackedQueries.Select(t => (t.Item1.GetTarget(), t.Item2)).Where(t => t.Item1 != null).ToList()!;
            }

            var solution = _workspace.CurrentSolution;
            var tasks = liveQueries.Select(t => PopulateContextGraphAsync(solution, t.Item2, t.Item1)).ToArray();
            var whenAllTask = Task.WhenAll(tasks);

            return whenAllTask.SafeContinueWith(t => PostUpdate(solution), TaskScheduler.Default);
        }

        private void PostUpdate(Solution solution)
        {
            _delay = null;
            lock (_gate)
            {
                // See if each context is still alive. It's possible it's already been GC'ed meaning we should stop caring about the query
                _trackedQueries.RemoveAll(t => !IsTrackingContext(t.Item1));
                if (_trackedQueries.IsEmpty())
                {
                    _workspace.WorkspaceChanged -= OnWorkspaceChanged;
                    return;
                }
            }

            EnqueueUpdateIfSolutionIsStale(solution);
        }

        private static bool IsTrackingContext(WeakReference<IGraphContext> weakContext)
        {
            var context = weakContext.GetTarget();
            return context != null && !context.CancelToken.IsCancellationRequested;
        }

        /// <summary>
        /// Populate the graph of the context with the values for the given Solution.
        /// </summary>
        private static async Task PopulateContextGraphAsync(
            Solution solution,
            ImmutableArray<IGraphQuery> graphQueries,
            IGraphContext context)
        {
            try
            {
                var cancellationToken = context.CancelToken;

                if (graphQueries.Length == 0)
                {
                    using var transaction = new GraphTransactionScope();
                    context.Graph.Links.Clear();
                    transaction.Complete();
                }
                else
                {
                    for (var i = 0; i < graphQueries.Length; i++)
                    {
                        var graphBuilder = await graphQueries[i].GetGraphAsync(solution, context, cancellationToken).ConfigureAwait(false);

                        using var transaction = new GraphTransactionScope();

                        if (i == 0)
                            context.Graph.Links.Clear();

                        graphBuilder.ApplyToGraph(context.Graph, cancellationToken);
                        context.OutputNodes.AddAll(graphBuilder.GetCreatedNodes(cancellationToken));

                        transaction.Complete();
                    }
                }
            }
            catch (Exception ex) when (FatalError.ReportAndPropagateUnlessCanceled(ex, ErrorSeverity.Diagnostic))
            {
                throw ExceptionUtilities.Unreachable();
            }
        }
    }
}
