using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParallelWorld.FireGraph
{
    /// <summary>
    /// Exact solver on the GPU (Resources/FireGraph/ExactFire.compute), R replicas at once.
    /// Build once per scenario and batch size, then call Run with new thresholds.
    /// </summary>
    public sealed class ExactFireGpu : IDisposable
    {
        public const int ThreadsPerGroup = 64;
        public const int MaxGroups = 65535;

        readonly FireScenario _s;
        readonly ComputeShader _cs;
        readonly int _replicas;
        readonly int _kInit, _kSeed, _kPredict, _kReduce, _kDecide, _kApply;
        readonly int[] _kernels;
        GraphicsBuffer _off, _src, _a, _b, _tau0, _tg, _td, _tx, _ign;
        GraphicsBuffer _ftp, _eth, _tIgn, _pred, _ignIter, _commit, _tmin, _done, _iters;
        readonly CommandBuffer _cmd = new CommandBuffer { name = "ExactFire" };

        public static bool Supported => SystemInfo.supportsComputeShaders;

        public ExactFireGpu(FireScenario s, int replicas, ComputeShader shader = null)
        {
            if (!Supported) throw new NotSupportedException("compute shaders are not supported on this device");
            int groups = (s.NodeCount + ThreadsPerGroup - 1) / ThreadsPerGroup;
            if (groups > MaxGroups) throw new ArgumentException($"{s.NodeCount} buildings exceed {MaxGroups * ThreadsPerGroup} per dispatch");
            if (replicas < 1 || replicas > MaxGroups) throw new ArgumentOutOfRangeException(nameof(replicas));
            _s = s;
            _replicas = replicas;
            _cs = shader != null ? shader : Resources.Load<ComputeShader>("FireGraph/ExactFire");
            if (_cs == null) throw new InvalidOperationException("Resources/FireGraph/ExactFire.compute not found");
            _kInit = _cs.FindKernel("Init");
            _kSeed = _cs.FindKernel("SeedIgnitions");
            _kPredict = _cs.FindKernel("Predict");
            _kReduce = _cs.FindKernel("Reduce");
            _kDecide = _cs.FindKernel("Decide");
            _kApply = _cs.FindKernel("Apply");
            _kernels = new[] { _kInit, _kSeed, _kPredict, _kReduce, _kDecide, _kApply };

            _off = Upload(s.InOffsets);
            _src = Upload(s.Src);
            _a = Upload(s.A);
            _b = Upload(s.B);
            _tau0 = Upload(s.Tau0);
            _tg = Upload(s.Tg);
            _td = Upload(s.Td);
            _tx = Upload(s.Tx);
            _ign = Upload(s.Ignitions);
            int total = s.NodeCount * replicas;
            _ftp = New(total); _eth = New(total); _tIgn = New(total); _pred = New(total);
            _ignIter = New(total); _commit = New(total);
            _tmin = New(replicas); _done = New(replicas); _iters = New(replicas);
            BindAll();
        }

        static GraphicsBuffer New(int count) => new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(count, 1), 4);

        static GraphicsBuffer Upload<T>(NativeArray<T> data) where T : struct
        {
            var buf = New(data.Length);
            if (data.Length > 0) buf.SetData(data);
            return buf;
        }

        void BindAll()
        {
            // Bind every buffer to every kernel: some D3D11 drivers reject a dispatch
            // when a declared UAV is unbound, even if the kernel does not touch it.
            foreach (int k in _kernels)
            {
                _cs.SetBuffer(k, "_Off", _off); _cs.SetBuffer(k, "_Src", _src);
                _cs.SetBuffer(k, "_A", _a); _cs.SetBuffer(k, "_B", _b);
                _cs.SetBuffer(k, "_Tau0", _tau0); _cs.SetBuffer(k, "_Tg", _tg);
                _cs.SetBuffer(k, "_Td", _td); _cs.SetBuffer(k, "_Tx", _tx);
                _cs.SetBuffer(k, "_Ign", _ign); _cs.SetBuffer(k, "_Ftp", _ftp); _cs.SetBuffer(k, "_Eth", _eth);
                _cs.SetBuffer(k, "_TIgn", _tIgn); _cs.SetBuffer(k, "_Pred", _pred);
                _cs.SetBuffer(k, "_IgnIter", _ignIter); _cs.SetBuffer(k, "_Commit", _commit);
                _cs.SetBuffer(k, "_TMin", _tmin); _cs.SetBuffer(k, "_Done", _done); _cs.SetBuffer(k, "_Iterations", _iters);
            }
        }

        /// <summary>
        /// Run all replicas to completion. ftp/eth are R*N thresholds (replica-major).
        /// iterationsPerSubmit iterations are recorded per command buffer before the
        /// CPU checks whether every replica has finished.
        /// </summary>
        public ExactFireCpu.Result Run(NativeArray<float> ftp, NativeArray<float> eth, CommitRule rule = CommitRule.Local,
            int iterationsPerSubmit = 32)
        {
            int n = _s.NodeCount, total = n * _replicas;
            if (ftp.Length != total || eth.Length != total) throw new ArgumentException("thresholds must be replicas * N long");
            _ftp.SetData(ftp);
            _eth.SetData(eth);
            _cs.SetInt("_N", n);
            _cs.SetInt("_R", _replicas);
            _cs.SetInt("_NumIgn", _s.Ignitions.Length);
            _cs.SetInt("_Rule", (int)rule);
            _cs.SetFloat("_QCr", _s.QCr);
            _cs.SetFloat("_NExp", _s.FtpN);
            _cs.SetFloat("_Horizon", _s.TEnd);
            _cs.SetFloat("_Lookahead", _s.Lookahead);

            int gx = (n + ThreadsPerGroup - 1) / ThreadsPerGroup;
            int gIgn = Math.Max(1, (_s.Ignitions.Length + ThreadsPerGroup - 1) / ThreadsPerGroup);
            _cs.Dispatch(_kInit, gx, _replicas, 1);
            if (_s.Ignitions.Length > 0) _cs.Dispatch(_kSeed, gIgn, _replicas, 1);

            var done = new uint[_replicas];
            int iter = 1;
            while (iter <= n + 1)
            {
                _cmd.Clear();
                for (int k = 0; k < iterationsPerSubmit && iter <= n + 1; k++, iter++)
                {
                    _cmd.SetComputeIntParam(_cs, "_Iter", iter);
                    _cmd.DispatchCompute(_cs, _kPredict, gx, _replicas, 1);
                    _cmd.DispatchCompute(_cs, _kReduce, _replicas, 1, 1);
                    _cmd.DispatchCompute(_cs, _kDecide, gx, _replicas, 1);
                    _cmd.DispatchCompute(_cs, _kApply, gx, _replicas, 1);
                }
                Graphics.ExecuteCommandBuffer(_cmd);
                _done.GetData(done);
                bool all = true;
                for (int r = 0; r < _replicas; r++) all &= done[r] != 0;
                if (all) break;
            }
            var tIgn = new float[total];
            var iters = new int[_replicas];
            _tIgn.GetData(tIgn);
            _iters.GetData(iters);
            return new ExactFireCpu.Result { TIgn = tIgn, Iterations = iters };
        }

        public void Dispose()
        {
            foreach (var b in new[] { _off, _src, _a, _b, _tau0, _tg, _td, _tx, _ign, _ftp, _eth, _tIgn, _pred, _ignIter, _commit, _tmin, _done, _iters })
                b?.Release();
            _cmd.Release();
        }
    }
}
