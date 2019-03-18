using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Python.Core;

namespace Microsoft.Python.LanguageServer.Indexing {
    internal interface IAsyncSymbols {
        Task<IReadOnlyList<HierarchicalSymbol>> GetSymbols(CancellationToken ct = default);
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
            throw new NotImplementedException();
        }

        public Task<IReadOnlyList<HierarchicalSymbol>> GetSymbols(CancellationToken ct = default) {
            var cancellableTcs = new TaskCompletionSource<IReadOnlyList<HierarchicalSymbol>>();
            _tcs.Task.ContinueWith(t => {
                if (t.IsCompletedSuccessfully) {
                    cancellableTcs.TrySetResult(t.Result);
                }
            }).DoNotWait();
            ct.Register(() => cancellableTcs.TrySetCanceled());
            return cancellableTcs.Task;
        }
    }
}
