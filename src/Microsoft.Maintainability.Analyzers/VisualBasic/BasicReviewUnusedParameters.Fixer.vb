' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Composition
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.CodeFixes

Namespace Microsoft.Maintainability.Analyzers
    ''' <summary>
    ''' CA1801: Review unused parameters
    ''' </summary>
    <ExportCodeFixProvider(LanguageNames.VisualBasic), [Shared]>
    Public NotInheritable Class BasicReviewUnusedParametersFixer
        Inherits ReviewUnusedParametersFixer

    End Class
End Namespace
