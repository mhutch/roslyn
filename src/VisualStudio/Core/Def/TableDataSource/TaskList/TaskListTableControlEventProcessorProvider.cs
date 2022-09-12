﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.TableDataSource
{
    [Export(typeof(ITableControlEventProcessorProvider))]
    [DataSourceType(StandardTableDataSources.CommentTableDataSource)]
    [DataSource(VisualStudioTaskListTableWorkspaceEventListener.IdentifierString)]
    [Name(Name)]
    [Order(Before = "default")]
    internal sealed class TaskListTableControlEventProcessorProvider : AbstractTableControlEventProcessorProvider<TaskListTableItem>
    {
        internal const string Name = "Task List Table Control Event Processor";

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public TaskListTableControlEventProcessorProvider()
        {
        }
    }
}
