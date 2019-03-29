using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Python.Core;
using Microsoft.Python.Core.Disposables;

namespace Microsoft.Python.LanguageServer.Indexing {
    internal interface IAsyncSymbols : IDisposable {
        Task<IReadOnlyList<HierarchicalSymbol>> GetSymbolsAsync(CancellationToken ct = default);
        void Cancel();
        void StartProcessing();
    }

    internal class StartedAsyncSymbols : IAsyncSymbols {
        private readonly TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>> _tcs;
        private readonly CancellationTokenSource _cts;
        private DisposeToken _disposeToken = DisposeToken.Create<PendingAsyncSymbols>();

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

        public void StartProcessing() {
            throw new InvalidOperationException("Symbol processing already started");
        }
    }

    internal class PendingAsyncSymbols : IAsyncSymbols {
        private readonly TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>> _tcs;
        private readonly CancellationTokenSource _cts;
        private readonly AsyncManualResetEvent _startedEvent;
        private DisposeToken _disposeToken = DisposeToken.Create<PendingAsyncSymbols>();

        public PendingAsyncSymbols(TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>> tcs, CancellationTokenSource cts,
            AsyncManualResetEvent startedEvent) {
            _tcs = tcs;
            _cts = cts;
            _startedEvent = startedEvent;
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

        public void StartProcessing() {
            _startedEvent.Set();
        }
    }
}
