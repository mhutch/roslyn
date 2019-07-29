﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.ExternalAccess.FSharp.Editor;
using Microsoft.CodeAnalysis.ExternalAccess.FSharp.Internal.Editor;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.ExternalAccess.FSharp.UnitTests
{
    public class FSharpInlineRenameReplacementKindTests
    {
        public static IEnumerable<object[]> enumValues()
        {
            foreach (var number in Enum.GetValues(typeof(FSharpInlineRenameReplacementKind)))
            {
                yield return new object[] { number };
            }
        }

        internal static InlineRenameReplacementKind GetExpectedInlineRenameReplacementKind(FSharpInlineRenameReplacementKind kind)
        {
            switch (kind)
            {
                case FSharpInlineRenameReplacementKind.NoConflict:
                    {
                        return InlineRenameReplacementKind.NoConflict;
                    }

                case FSharpInlineRenameReplacementKind.ResolvedReferenceConflict:
                    {
                        return InlineRenameReplacementKind.ResolvedReferenceConflict;
                    }

                case FSharpInlineRenameReplacementKind.ResolvedNonReferenceConflict:
                    {
                        return InlineRenameReplacementKind.ResolvedNonReferenceConflict;
                    }

                case FSharpInlineRenameReplacementKind.UnresolvedConflict:
                    {
                        return InlineRenameReplacementKind.UnresolvedConflict;
                    }

                case FSharpInlineRenameReplacementKind.Complexified:
                    {
                        return InlineRenameReplacementKind.Complexified;
                    }

                default:
                    {
                        throw ExceptionUtilities.UnexpectedValue(kind);
                    }
            }
        }

        [Theory]
        [MemberData(nameof(enumValues))]
        internal void MapsCorrectly(FSharpInlineRenameReplacementKind kind)
        {
            var actual = FSharpInlineRenameReplacementKindHelpers.ConvertTo(kind);
            var expected = GetExpectedInlineRenameReplacementKind(kind);
            Assert.Equal(expected, actual);
        }
    }
}
