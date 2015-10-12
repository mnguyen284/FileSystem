// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNet.FileProviders
{
    public class EmbeddedFileProvider : IFileProvider
    {
        private readonly IFileProvider _defaultProvider;
        private readonly IEnumerable<SingleAssemblyEmbeddedFileProvider> _embeddedFileProviders;

        public EmbeddedFileProvider(Assembly assembly)
            : this(assembly, string.Empty)
        { }

        public EmbeddedFileProvider(IFileProvider defaultProvider, Assembly assembly)
            : this(defaultProvider, assembly, string.Empty)
        { }

        public EmbeddedFileProvider(Assembly assembly, string baseNamespace)
            : this(new EmbeddedFileProviderAssemblyOptions(assembly, baseNamespace))
        { }

        public EmbeddedFileProvider(IFileProvider defaultProvider, Assembly assembly, string baseNamespace)
            : this(defaultProvider, new EmbeddedFileProviderAssemblyOptions(assembly, baseNamespace))
        { }

        public EmbeddedFileProvider(params EmbeddedFileProviderAssemblyOptions[] assemblies)
            : this(null, assemblies)
        { }

        public EmbeddedFileProvider(IFileProvider defaultProvider, params EmbeddedFileProviderAssemblyOptions[] assemblies)
        {
            if (assemblies == null)
            {
                throw new ArgumentNullException(nameof(assemblies));
            }

            if (defaultProvider == null && assemblies.Length == 0)
            {
                throw new InvalidOperationException("There must be at least 1 default provider or assembly specified."); // REVIEW: Revise error message
            }

            _defaultProvider = defaultProvider;

            var embeddedFileProviders = new List<SingleAssemblyEmbeddedFileProvider>();
            foreach (var assembly in assemblies)
            {
                embeddedFileProviders.Add(new SingleAssemblyEmbeddedFileProvider(assembly.Assembly, assembly.BaseNamespace));
            }
            // If subpath is found in multiple assemblies, result found in the last assembly will be returned.
            embeddedFileProviders.Reverse();
            _embeddedFileProviders = embeddedFileProviders;
        }

        public IFileInfo GetFileInfo(string subpath)
        {
            var defaultFileInfo = _defaultProvider == null ? null : _defaultProvider.GetFileInfo(subpath);
            if (_defaultProvider == null || !defaultFileInfo.Exists)
            {
                foreach (var provider in _embeddedFileProviders)
                {
                    var embeddedFileInfo = provider.GetFileInfo(subpath);
                    if (embeddedFileInfo.Exists)
                    {
                        return embeddedFileInfo;
                    }
                }
            }
            return _defaultProvider == null ? new NotFoundFileInfo(subpath) : defaultFileInfo;
        }

        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            var defaultContents = _defaultProvider == null ? null : _defaultProvider.GetDirectoryContents(subpath);
            var assembliesContents = new List<IDirectoryContents>();

            foreach (var provider in _embeddedFileProviders)
            {
                var assemblyContents = provider.GetDirectoryContents(subpath);
                if (assemblyContents.Exists)
                {
                    assembliesContents.Add(assemblyContents);
                }
            }

            var contentsLists = new List<IDirectoryContents>();
            if (_defaultProvider != null && defaultContents.Exists)
            {
                contentsLists.Add(defaultContents);
            }
            contentsLists.AddRange(assembliesContents);

            if (contentsLists.Count > 0)
            {
                var entries = MergeDirectoryContents(contentsLists.ToArray());
                return new EnumerableDirectoryContents(entries);
            }

            return _defaultProvider == null ? new NotFoundDirectoryContents() : defaultContents;
        }

        public IChangeToken Watch(string filter)
        {
            if (_defaultProvider != null)
            {
                return _defaultProvider.Watch(filter);
            }

            return NoopChangeToken.Singleton;
        }

        private IEnumerable<IFileInfo> MergeDirectoryContents(params IDirectoryContents[] contentsLists)
        {
            var entries = new List<IFileInfo>();
            for (var i = 0; i < contentsLists.Length; i++)
            {
                var directoryContents = contentsLists[i];
                // Use all contents from the first list because there would be no duplication within a list
                if (i == 0)
                {
                    entries.AddRange(directoryContents);
                }
                else
                {
                    foreach (var entry in directoryContents)
                    {
                        if (!entries.Any(e => e.Name == entry.Name))
                        {
                            entries.Add(entry);
                        }
                    }
                }
            }
            entries = entries.OrderBy(m => m.Name).ToList();
            return entries;
        }

        /// <summary>
        /// Looks up files using embedded resources in the specified assembly.
        /// This file provider is case sensitive.
        /// </summary>
        private class SingleAssemblyEmbeddedFileProvider : IFileProvider
        {
            private readonly Assembly _assembly;
            private readonly string _baseNamespace;
            private readonly DateTimeOffset _lastModified;

            /// <summary>
            /// Initializes a new instance of the <see cref="SingleAssemblyEmbeddedFileProvider" /> class using the specified
            /// assembly and empty base namespace.
            /// </summary>
            /// <param name="assembly"></param>
            public SingleAssemblyEmbeddedFileProvider(Assembly assembly)
                : this(assembly, string.Empty)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="SingleAssemblyEmbeddedFileProvider" /> class using the specified
            /// assembly and base namespace.
            /// </summary>
            /// <param name="assembly">The assembly that contains the embedded resources.</param>
            /// <param name="baseNamespace">The base namespace that contains the embedded resources.</param>
            public SingleAssemblyEmbeddedFileProvider(Assembly assembly, string baseNamespace)
            {
                if (assembly == null)
                {
                    throw new ArgumentNullException("assembly");
                }

                _baseNamespace = string.IsNullOrEmpty(baseNamespace) ? string.Empty : baseNamespace + ".";
                _assembly = assembly;
                // REVIEW: Does this even make sense?
                _lastModified = DateTimeOffset.MaxValue;
            }

            /// <summary>
            /// Locates a file at the given path.
            /// </summary>
            /// <param name="subpath">The path that identifies the file. </param>
            /// <returns>The file information. Caller must check Exists property.</returns>
            public IFileInfo GetFileInfo(string subpath)
            {
                if (string.IsNullOrEmpty(subpath))
                {
                    return new NotFoundFileInfo(subpath);
                }

                // Relative paths starting with a leading slash okay
                if (subpath.StartsWith("/", StringComparison.Ordinal))
                {
                    subpath = subpath.Substring(1);
                }

                string resourcePath = _baseNamespace + subpath.Replace('/', '.');
                string name = Path.GetFileName(subpath);
                if (_assembly.GetManifestResourceInfo(resourcePath) == null)
                {
                    return new NotFoundFileInfo(name);
                }
                return new EmbeddedResourceFileInfo(_assembly, resourcePath, name, _lastModified);
            }

            /// <summary>
            /// Enumerate a directory at the given path, if any.
            /// This file provider uses a flat directory structure. Everything under the base namespace is considered to be one directory.
            /// </summary>
            /// <param name="subpath">The path that identifies the directory</param>
            /// <returns>Contents of the directory. Caller must check Exists property.</returns>
            public IDirectoryContents GetDirectoryContents(string subpath)
            {
                // The file name is assumed to be the remainder of the resource name.
                if (subpath == null)
                {
                    return new NotFoundDirectoryContents();
                }

                // Relative paths starting with a leading slash okay
                if (subpath.StartsWith("/", StringComparison.Ordinal))
                {
                    subpath = subpath.Substring(1);
                }

                // Non-hierarchal.
                if (!subpath.Equals(string.Empty))
                {
                    return new NotFoundDirectoryContents();
                }

                IList<IFileInfo> entries = new List<IFileInfo>();

                // TODO: The list of resources in an assembly isn't going to change. Consider caching.
                string[] resources = _assembly.GetManifestResourceNames();
                for (int i = 0; i < resources.Length; i++)
                {
                    string resourceName = resources[i];
                    if (resourceName.StartsWith(_baseNamespace))
                    {
                        entries.Add(new EmbeddedResourceFileInfo(
                            _assembly, resourceName, resourceName.Substring(_baseNamespace.Length), _lastModified));
                    }
                }

                return new EnumerableDirectoryContents(entries);
            }

            public IChangeToken Watch(string pattern)
            {
                return NoopChangeToken.Singleton;
            }

            private class EmbeddedResourceFileInfo : IFileInfo
            {
                private readonly Assembly _assembly;
                private readonly DateTimeOffset _lastModified;
                private readonly string _resourcePath;
                private readonly string _name;

                private long? _length;

                public EmbeddedResourceFileInfo(Assembly assembly, string resourcePath, string name, DateTimeOffset lastModified)
                {
                    _assembly = assembly;
                    _lastModified = lastModified;
                    _resourcePath = resourcePath;
                    _name = name;
                }

                public bool Exists
                {
                    get { return true; }
                }

                public long Length
                {
                    get
                    {
                        if (!_length.HasValue)
                        {
                            using (Stream stream = _assembly.GetManifestResourceStream(_resourcePath))
                            {
                                _length = stream.Length;
                            }
                        }
                        return _length.Value;
                    }
                }

                // Not directly accessible.
                public string PhysicalPath
                {
                    get { return null; }
                }

                public string Name
                {
                    get { return _name; }
                }

                public DateTimeOffset LastModified
                {
                    get { return _lastModified; }
                }

                public bool IsDirectory
                {
                    get { return false; }
                }

                public Stream CreateReadStream()
                {
                    Stream stream = _assembly.GetManifestResourceStream(_resourcePath);
                    if (!_length.HasValue)
                    {
                        _length = stream.Length;
                    }
                    return stream;
                }
            }
        }
    }
}
