// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Python.Analysis.Documents;
using Microsoft.Python.Core;
using Microsoft.Python.Core.Disposables;
using Microsoft.Python.Core.IO;
using Microsoft.Python.Parsing;

namespace Microsoft.Python.LanguageServer.Indexing {
    internal sealed class SymbolIndex : ISymbolIndex {
        private readonly DisposableBag _disposables = new DisposableBag(nameof(SymbolIndex));
        private readonly ConcurrentDictionary<string, IAsyncSymbols> _index = 
            new ConcurrentDictionary<string, IAsyncSymbols>(PathEqualityComparer.Instance);

        private readonly IIndexParser _indexParser;
        private readonly ConcurrentDictionary<string, TaskData> ReIndexData = 
            new ConcurrentDictionary<string, TaskData>(PathEqualityComparer.Instance);

        private class TaskData {
            public TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>> Tcs;
            public CancellationTokenSource Cts;
        }

        public SymbolIndex(IFileSystem fileSystem, PythonLanguageVersion version) {
            _indexParser = new IndexParser(fileSystem, version);
            _disposables
                .Add(_indexParser)
                .Add(() => {
                    foreach (var recentSymbols in _index.Values) {
                        recentSymbols.Dispose();
                    }
                });
        }

        public Task<IReadOnlyList<HierarchicalSymbol>> HierarchicalDocumentSymbolsAsync(string path, CancellationToken ct = default) {
            if (_index.TryGetValue(path, out var x)) {
                return x.GetSymbolsAsync(ct);
            } else {
                return Task.FromResult<IReadOnlyList<HierarchicalSymbol>>(new List<HierarchicalSymbol>());
            }
        }

        public async Task<IReadOnlyList<FlatSymbol>> WorkspaceSymbolsAsync(string query, int maxLength, CancellationToken ct = default) {
            var results = new List<FlatSymbol>();
            foreach (var asyncSymbols in _index) {
                var symbols = await asyncSymbols.Value.GetSymbolsAsync(ct);
                results.AddRange(WorkspaceSymbolsQuery(asyncSymbols.Key, query, symbols).Take(maxLength - results.Count));
                ct.ThrowIfCancellationRequested();
                if (results.Count >= maxLength) {
                    break;
                }
            }
            return results;
        }

        public void Add(string path, IDocument doc) {
            var cts = new CancellationTokenSource();
            var tcs = new TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>>();
            var newAsyncSyms = new StartedAsyncSymbols(tcs, cts);
            IndexAsync(doc, tcs, cts.Token).DoNotWait();
            _index.AddOrUpdate(path, newAsyncSyms, (_, old) => { old.Cancel(); return newAsyncSyms; });
        }

        public void Parse(string path) {
            var cts = new CancellationTokenSource();
            var tcs = new TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>>();
            var newAsyncSyms = new StartedAsyncSymbols(tcs, cts);
            ParseAsync(path, tcs, cts.Token).DoNotWait();
            _index.AddOrUpdate(path, newAsyncSyms, (_, old) => { old.Cancel(); return newAsyncSyms; });
        }
        
        public void Delete(string path) {
            if (_index.TryRemove(path, out var asyncSymbols)) {
                asyncSymbols.Cancel();
            } else {
                throw new Exception($"Tried to remove {path} when it's not indexed");
            }
        }

        public void ReIndex(string path, IDocument doc) {
            
            if (ReIndexData.TryRemove(path, out var data)) {
                IndexAsync(doc, data.Tcs, data.Cts.Token).DoNotWait();
            }
            
        }

        public void MarkAsPending(IDocument doc) {
            
            var data = ReIndexData.GetOrAdd(doc.Uri.AbsolutePath, (_) => new TaskData() {
                Tcs = new TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>>(),
                Cts = new CancellationTokenSource()
            });
            var newAsyncSyms = new StartedAsyncSymbols(data.Tcs, data.Cts);
            _index.AddOrUpdate(doc.Uri.AbsolutePath, newAsyncSyms, (_, old) => { old.Cancel(); return newAsyncSyms; });
        }

        private async Task IndexAsync(IDocument doc, TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>> tcs,
            CancellationToken indexCt) {
            var ast = await doc.GetAstAsync(indexCt);
            indexCt.ThrowIfCancellationRequested();
            var walker = new SymbolIndexWalker(ast);
            ast.Walk(walker);
            tcs.SetResult(walker.Symbols);
        }


        private async Task ParseAsync(string path, TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>> tcs,
            CancellationToken ct) {
            try {
                var ast = await _indexParser.ParseAsync(path, ct);
                ct.ThrowIfCancellationRequested();
                var walker = new SymbolIndexWalker(ast);
                ast.Walk(walker);
                tcs.TrySetResult(walker.Symbols);
            } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) {
                Trace.TraceError(e.Message);
            }

            tcs.TrySetResult(new List<HierarchicalSymbol>());
        }

        private IEnumerable<FlatSymbol> WorkspaceSymbolsQuery(string path, string query,
            IReadOnlyList<HierarchicalSymbol> symbols) {
            var rootSymbols = DecorateWithParentsName(symbols, null);
            var treeSymbols = rootSymbols.TraverseBreadthFirst((symAndPar) => {
                var sym = symAndPar.symbol;
                return DecorateWithParentsName((sym.Children ?? Enumerable.Empty<HierarchicalSymbol>()).ToList(), sym.Name);
            });
            return treeSymbols.Where(sym => sym.symbol.Name.ContainsOrdinal(query, ignoreCase: true))
                              .Select(sym => new FlatSymbol(sym.symbol.Name, sym.symbol.Kind, path, sym.symbol.SelectionRange, sym.parentName));
        }

        private static IEnumerable<(HierarchicalSymbol symbol, string parentName)> DecorateWithParentsName(
            IEnumerable<HierarchicalSymbol> symbols, string parentName) {
            return symbols.Select((symbol) => (symbol, parentName)).ToList();
        }

        public void Dispose() {
            _disposables.TryDispose();
        }
    }
}
