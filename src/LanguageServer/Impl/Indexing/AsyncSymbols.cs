using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Python.Core;
using Microsoft.Python.Core.Disposables;

namespace Microsoft.Python.LanguageServer.Indexing {
    internal interface IAsyncSymbols : IDisposable {
        Task<IReadOnlyList<HierarchicalSymbol>> GetSymbolsAsync(CancellationToken ct = default);
        void Cancel();
    }

    internal class StartedAsyncSymbols : IAsyncSymbols {
        private readonly TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>> _tcs;
        private readonly CancellationTokenSource _cts;

        public StartedAsyncSymbols(TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>> tcs, CancellationTokenSource cts) {
            _tcs = tcs;
            _cts = cts;
        }

        public void Cancel() {
            _cts.Cancel();
            _tcs.TrySetCanceled();
        }

        public void Dispose() {
            _cts.Dispose();
        }

        public Task<IReadOnlyList<HierarchicalSymbol>> GetSymbolsAsync(CancellationToken ct = default) {
            return _tcs.Task.ContinueWith(t => t.GetAwaiter().GetResult(), ct);
        }
    }
}
