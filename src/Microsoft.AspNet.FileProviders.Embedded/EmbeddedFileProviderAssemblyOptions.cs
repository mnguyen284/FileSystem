// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Reflection;

namespace Microsoft.AspNet.FileProviders
{
    public class EmbeddedFileProviderAssemblyOptions
    {
        public EmbeddedFileProviderAssemblyOptions(Assembly assembly, string baseNamespace)
        {
            Assembly = assembly;
            BaseNamespace = baseNamespace;
        }

        public Assembly Assembly { get; set; }

        public string BaseNamespace { get; set; }
    }
}
