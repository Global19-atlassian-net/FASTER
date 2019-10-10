﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

#pragma warning disable 0162

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.core
{

    /// <summary>
    /// FASTER log
    /// </summary>
    public class FasterLog : IDisposable
    {
        private readonly BlittableAllocator<Empty, byte> allocator;
        private readonly LightEpoch epoch;
        private readonly ILogCommitManager logCommitManager;
        private readonly GetMemory getMemory;
        private TaskCompletionSource<long> commitTcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Beginning address of log
        /// </summary>
        public long BeginAddress => allocator.BeginAddress;

        /// <summary>
        /// Tail address of log
        /// </summary>
        public long TailAddress => allocator.GetTailAddress();

        /// <summary>
        /// Log flushed until address
        /// </summary>
        public long FlushedUntilAddress => allocator.FlushedUntilAddress;

        /// <summary>
        /// Log committed until address
        /// </summary>
        public long CommittedUntilAddress;

        /// <summary>
        /// Log committed begin address
        /// </summary>
        public long CommittedBeginAddress;

        /// <summary>
        /// Task notifying commit completions
        /// </summary>
        internal Task<long> CommitTask => commitTcs.Task;

        /// <summary>
        /// Create new log instance
        /// </summary>
        /// <param name="logSettings"></param>
        public FasterLog(FasterLogSettings logSettings)
        {
            logCommitManager = logSettings.LogCommitManager ?? 
                new LocalLogCommitManager(logSettings.LogCommitFile ??
                logSettings.LogDevice.FileName + ".commit");

            getMemory = logSettings.GetMemory;
            epoch = new LightEpoch();
            CommittedUntilAddress = Constants.kFirstValidAddress;
            CommittedBeginAddress = Constants.kFirstValidAddress;

            allocator = new BlittableAllocator<Empty, byte>(
                logSettings.GetLogSettings(), null, 
                null, epoch, e => CommitCallback(e));
            allocator.Initialize();
            Restore();
        }

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
            allocator.Dispose();
            epoch.Dispose();
            commitTcs.TrySetException(new ObjectDisposedException("Log has been disposed"));
        }

        #region Enqueue
        /// <summary>
        /// Enqueue entry to log (in memory) - no guarantee of flush/commit
        /// </summary>
        /// <param name="entry">Entry to be enqueued to log</param>
        /// <returns>Logical address of added entry</returns>
        public long Enqueue(byte[] entry)
        {
            long logicalAddress;
            while (!TryEnqueue(entry, out logicalAddress)) ;
            return logicalAddress;
        }

        /// <summary>
        /// Enqueue entry to log (in memory) - no guarantee of flush/commit
        /// </summary>
        /// <param name="entry">Entry to be enqueued to log</param>
        /// <returns>Logical address of added entry</returns>
        public long Enqueue(ReadOnlySpan<byte> entry)
        {
            long logicalAddress;
            while (!TryEnqueue(entry, out logicalAddress)) ;
            return logicalAddress;
        }

        /// <summary>
        /// Enqueue batch of entries to log (in memory) - no guarantee of flush/commit
        /// </summary>
        /// <param name="readOnlySpanBatch">Batch of entries to be enqueued to log</param>
        /// <returns>Logical address of added entry</returns>
        public long Enqueue(IReadOnlySpanBatch readOnlySpanBatch)
        {
            long logicalAddress;
            while (!TryEnqueue(readOnlySpanBatch, out logicalAddress)) ;
            return logicalAddress;
        }
        #endregion

        #region TryEnqueue
        /// <summary>
        /// Try to enqueue entry to log (in memory). If it returns true, we are
        /// done. If it returns false, we need to retry.
        /// </summary>
        /// <param name="entry">Entry to be enqueued to log</param>
        /// <param name="logicalAddress">Logical address of added entry</param>
        /// <returns>Whether the append succeeded</returns>
        public unsafe bool TryEnqueue(byte[] entry, out long logicalAddress)
        {
            logicalAddress = 0;

            epoch.Resume();

            var length = entry.Length;
            logicalAddress = allocator.TryAllocate(4 + Align(length));
            if (logicalAddress == 0)
            {
                epoch.Suspend();
                return false;
            }

            var physicalAddress = allocator.GetPhysicalAddress(logicalAddress);
            *(int*)physicalAddress = length;
            fixed (byte* bp = entry)
                Buffer.MemoryCopy(bp, (void*)(4 + physicalAddress), length, length);

            epoch.Suspend();
            return true;
        }

        /// <summary>
        /// Try to append entry to log. If it returns true, we are
        /// done. If it returns false, we need to retry.
        /// </summary>
        /// <param name="entry">Entry to be appended to log</param>
        /// <param name="logicalAddress">Logical address of added entry</param>
        /// <returns>Whether the append succeeded</returns>
        public unsafe bool TryEnqueue(ReadOnlySpan<byte> entry, out long logicalAddress)
        {
            logicalAddress = 0;

            epoch.Resume();

            var length = entry.Length;
            logicalAddress = allocator.TryAllocate(4 + Align(length));
            if (logicalAddress == 0)
            {
                epoch.Suspend();
                return false;
            }

            var physicalAddress = allocator.GetPhysicalAddress(logicalAddress);
            *(int*)physicalAddress = length;
            fixed (byte* bp = &entry.GetPinnableReference())
                Buffer.MemoryCopy(bp, (void*)(4 + physicalAddress), length, length);

            epoch.Suspend();
            return true;
        }

        /// <summary>
        /// Try to enqueue batch of entries as a single atomic unit (to memory). Entire 
        /// batch needs to fit on one log page.
        /// </summary>
        /// <param name="readOnlySpanBatch">Batch to be appended to log</param>
        /// <param name="logicalAddress">Logical address of first added entry</param>
        /// <returns>Whether the append succeeded</returns>
        public bool TryEnqueue(IReadOnlySpanBatch readOnlySpanBatch, out long logicalAddress)
        {
            return TryAppend(readOnlySpanBatch, out logicalAddress, out _);
        }
        #endregion

        #region EnqueueAsync
        /// <summary>
        /// Enqueue entry to log in memory (async) - completes after entry is 
        /// appended to memory, NOT committed to storage.
        /// </summary>
        /// <param name="entry"></param>
        /// <returns></returns>
        public async ValueTask<long> EnqueueAsync(byte[] entry)
        {
            long logicalAddress;

            while (true)
            {
                var task = CommitTask;
                if (TryEnqueue(entry, out logicalAddress))
                    break;
                await task;
            }

            return logicalAddress;
        }

        /// <summary>
        /// Enqueue entry to log in memory (async) - completes after entry is 
        /// appended to memory, NOT committed to storage.
        /// </summary>
        /// <param name="entry"></param>
        /// <returns></returns>
        public async ValueTask<long> EnqueueAsync(ReadOnlyMemory<byte> entry)
        {
            long logicalAddress;

            while (true)
            {
                var task = CommitTask;
                if (TryEnqueue(entry.Span, out logicalAddress))
                    break;
                await task;
            }

            return logicalAddress;
        }

        /// <summary>
        /// Enqueue batch of entries to log in memory (async) - completes after entry is 
        /// appended to memory, NOT committed to storage.
        /// </summary>
        /// <param name="readOnlySpanBatch"></param>
        /// <returns></returns>
        public async ValueTask<long> EnqueueAsync(IReadOnlySpanBatch readOnlySpanBatch)
        {
            long logicalAddress;

            while (true)
            {
                var task = CommitTask;
                if (TryEnqueue(readOnlySpanBatch, out logicalAddress))
                    break;
                await task;
            }

            return logicalAddress;
        }
        #endregion

        #region WaitForCommit and WaitForCommitAsync

        /// <summary>
        /// Spin-wait for enqueues, until tail or specified address, to commit to 
        /// storage. Does NOT itself issue a commit, just waits for commit. So you should 
        /// ensure that someone else causes the commit to happen.
        /// </summary>
        /// <param name="untilAddress">Address until which we should wait for commit, default 0 for tail of log</param>
        /// <returns></returns>
        public void WaitForCommit(long untilAddress = 0)
        {
            var tailAddress = untilAddress;
            if (tailAddress == 0) tailAddress = allocator.GetTailAddress();

            while (CommittedUntilAddress < tailAddress) ;
        }

        /// <summary>
        /// Wait for appends (in memory), until tail or specified address, to commit to 
        /// storage. Does NOT itself issue a commit, just waits for commit. So you should 
        /// ensure that someone else causes the commit to happen.
        /// </summary>
        /// <param name="untilAddress">Address until which we should wait for commit, default 0 for tail of log</param>
        /// <returns></returns>
        public async ValueTask WaitForCommitAsync(long untilAddress = 0)
        {
            var tailAddress = untilAddress;
            if (tailAddress == 0) tailAddress = allocator.GetTailAddress();

            while (true)
            {
                var task = CommitTask;
                if (CommittedUntilAddress < tailAddress)
                {
                    await task;
                }
                else
                    break;
            }
        }
        #endregion

        #region Commit

        /// <summary>
        /// Issue commit request for log (until tail)
        /// </summary>
        /// <param name="spinWait">If true, spin-wait until commit completes. Otherwise, issue commit and return immediately.</param>
        /// <returns></returns>
        public void Commit(bool spinWait = false)
        {
            CommitInternal(spinWait);
        }

        /// <summary>
        /// Async commit log (until tail), completes only when we 
        /// complete the commit
        /// </summary>
        /// <returns></returns>
        public async ValueTask CommitAsync()
        {
            var tailAddress = CommitInternal();

            while (true)
            {
                var task = CommitTask;
                if (CommittedUntilAddress < tailAddress)
                {
                    await task;
                }
                else
                    break;
            }
        }

        #endregion

        #region EnqueueAndWaitForCommit

        /// <summary>
        /// Append entry to log - spin-waits until entry is committed to storage.
        /// Does NOT itself issue flush!
        /// </summary>
        /// <param name="entry"></param>
        /// <returns></returns>
        public long EnqueueAndWaitForCommit(byte[] entry)
        {
            long logicalAddress;
            while (!TryEnqueue(entry, out logicalAddress)) ;
            while (CommittedUntilAddress < logicalAddress + 4 + entry.Length) ;
            return logicalAddress;
        }

        /// <summary>
        /// Append entry to log - spin-waits until entry is committed to storage.
        /// Does NOT itself issue flush!
        /// </summary>
        /// <param name="entry"></param>
        /// <returns></returns>
        public long EnqueueAndWaitForCommit(ReadOnlySpan<byte> entry)
        {
            long logicalAddress;
            while (!TryEnqueue(entry, out logicalAddress)) ;
            while (CommittedUntilAddress < logicalAddress + 4 + entry.Length) ;
            return logicalAddress;
        }

        /// <summary>
        /// Append batch of entries to log - spin-waits until entry is committed to storage.
        /// Does NOT itself issue flush!
        /// </summary>
        /// <param name="readOnlySpanBatch"></param>
        /// <returns></returns>
        public long EnqueueAndWaitForCommit(IReadOnlySpanBatch readOnlySpanBatch)
        {
            long logicalAddress;
            while (!TryEnqueue(readOnlySpanBatch, out logicalAddress)) ;
            while (CommittedUntilAddress < logicalAddress + 1) ;
            return logicalAddress;
        }

        #endregion

        #region EnqueueAndWaitForCommitAsync

        /// <summary>
        /// Append entry to log (async) - completes after entry is committed to storage.
        /// Does NOT itself issue flush!
        /// </summary>
        /// <param name="entry"></param>
        /// <returns></returns>
        public async ValueTask<long> EnqueueAndWaitForCommitAsync(byte[] entry)
        {
            long logicalAddress;

            // Phase 1: wait for commit to memory
            while (true)
            {
                var task = CommitTask;
                if (TryEnqueue(entry, out logicalAddress))
                    break;
                await task;
            }

            // Phase 2: wait for commit/flush to storage
            while (true)
            {
                var task = CommitTask;
                if (CommittedUntilAddress < logicalAddress + 4 + entry.Length)
                {
                    await task;
                }
                else
                    break;
            }

            return logicalAddress;
        }

        /// <summary>
        /// Append entry to log (async) - completes after entry is committed to storage.
        /// Does NOT itself issue flush!
        /// </summary>
        /// <param name="entry"></param>
        /// <returns></returns>
        public async ValueTask<long> EnqueueAndWaitForCommitAsync(ReadOnlyMemory<byte> entry)
        {
            long logicalAddress;

            // Phase 1: wait for commit to memory
            while (true)
            {
                var task = CommitTask;
                if (TryEnqueue(entry.Span, out logicalAddress))
                    break;
                await task;
            }

            // Phase 2: wait for commit/flush to storage
            while (true)
            {
                var task = CommitTask;
                if (CommittedUntilAddress < logicalAddress + 4 + entry.Length)
                {
                    await task;
                }
                else
                    break;
            }

            return logicalAddress;
        }

        /// <summary>
        /// Append batch of entries to log (async) - completes after batch is committed to storage.
        /// Does NOT itself issue flush!
        /// </summary>
        /// <param name="readOnlySpanBatch"></param>
        /// <returns></returns>
        public async ValueTask<long> EnqueueAndWaitForCommitAsync(IReadOnlySpanBatch readOnlySpanBatch)
        {
            long logicalAddress;
            int allocatedLength;

            // Phase 1: wait for commit to memory
            while (true)
            {
                var task = CommitTask;
                if (TryAppend(readOnlySpanBatch, out logicalAddress, out allocatedLength))
                    break;
                await task;
            }

            // Phase 2: wait for commit/flush to storage
            while (true)
            {
                var task = CommitTask;
                if (CommittedUntilAddress < logicalAddress + allocatedLength)
                {
                    await task;
                }
                else
                    break;
            }

            return logicalAddress;
        }
        #endregion

        /// <summary>
        /// Truncate the log until, but not including, untilAddress
        /// </summary>
        /// <param name="untilAddress"></param>
        public void TruncateUntil(long untilAddress)
        {
            allocator.ShiftBeginAddress(untilAddress);
        }

        /// <summary>
        /// Pull-based iterator interface for scanning FASTER log
        /// </summary>
        /// <param name="beginAddress">Begin address for scan</param>
        /// <param name="endAddress">End address for scan (or long.MaxValue for tailing)</param>
        /// <param name="scanBufferingMode">Use single or double buffering</param>
        /// <returns></returns>
        public FasterLogScanIterator Scan(long beginAddress, long endAddress, ScanBufferingMode scanBufferingMode = ScanBufferingMode.DoublePageBuffering)
        {
            return new FasterLogScanIterator(this, allocator, beginAddress, endAddress, getMemory, scanBufferingMode, epoch);
        }

        /// <summary>
        /// Random read record from log, at given address
        /// </summary>
        /// <param name="address">Logical address to read from</param>
        /// <param name="estimatedLength">Estimated length of entry, if known</param>
        /// <returns></returns>
        public async ValueTask<(byte[], int)> ReadAsync(long address, int estimatedLength = 0)
        {
            epoch.Resume();
            if (address >= CommittedUntilAddress || address < BeginAddress)
            {
                epoch.Suspend();
                return default;
            }
            var ctx = new SimpleReadContext
            {
                logicalAddress = address,
                completedRead = new SemaphoreSlim(0)
            };
            unsafe
            {
                allocator.AsyncReadRecordToMemory(address, 4 + estimatedLength, AsyncGetFromDiskCallback, ref ctx);
            }
            epoch.Suspend();
            await ctx.completedRead.WaitAsync();
            return GetRecordAndFree(ctx.record);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Align(int length)
        {
            return (length + 3) & ~3;
        }

        /// <summary>
        /// Commit log
        /// </summary>
        private void CommitCallback(long flushAddress)
        {
            long beginAddress = allocator.BeginAddress;
            TaskCompletionSource<long> _commitTcs = default;

            // We can only allow serial monotonic synchronous commit
            lock (this)
            {
                if ((beginAddress > CommittedBeginAddress) || (flushAddress > CommittedUntilAddress))
                {
                    FasterLogRecoveryInfo info = new FasterLogRecoveryInfo
                    {
                        BeginAddress = beginAddress > CommittedBeginAddress ? beginAddress : CommittedBeginAddress,
                        FlushedUntilAddress = flushAddress > CommittedUntilAddress ? flushAddress : CommittedUntilAddress
                    };

                    logCommitManager.Commit(info.BeginAddress, info.FlushedUntilAddress, info.ToByteArray());
                    CommittedBeginAddress = info.BeginAddress;
                    CommittedUntilAddress = info.FlushedUntilAddress;

                    _commitTcs = commitTcs;
                    if (commitTcs.Task.Status != TaskStatus.Faulted)
                    {
                        commitTcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
            }
            _commitTcs?.TrySetResult(flushAddress);
        }

        /// <summary>
        /// Restore log
        /// </summary>
        private void Restore()
        {
            FasterLogRecoveryInfo info = new FasterLogRecoveryInfo();
            var commitInfo = logCommitManager.GetCommitMetadata();

            if (commitInfo == null) return;

            using (var r = new BinaryReader(new MemoryStream(commitInfo)))
            {
                info.Initialize(r);
            }

            var headAddress = info.FlushedUntilAddress - allocator.GetOffsetInPage(info.FlushedUntilAddress);
            if (headAddress == 0) headAddress = Constants.kFirstValidAddress;

            allocator.RestoreHybridLog(info.FlushedUntilAddress, headAddress, info.BeginAddress);
            CommittedUntilAddress = info.FlushedUntilAddress;
            CommittedBeginAddress = info.BeginAddress;
        }

        /// <summary>
        /// Try to append batch of entries as a single atomic unit. Entire batch
        /// needs to fit on one page.
        /// </summary>
        /// <param name="readOnlySpanBatch">Batch to be appended to log</param>
        /// <param name="logicalAddress">Logical address of first added entry</param>
        /// <param name="allocatedLength">Actual allocated length</param>
        /// <returns>Whether the append succeeded</returns>
        private unsafe bool TryAppend(IReadOnlySpanBatch readOnlySpanBatch, out long logicalAddress, out int allocatedLength)
        {
            logicalAddress = 0;

            int totalEntries = readOnlySpanBatch.TotalEntries();
            allocatedLength = 0;
            for (int i = 0; i < totalEntries; i++)
            {
                allocatedLength += Align(readOnlySpanBatch.Get(i).Length) + 4;
            }

            epoch.Resume();

            logicalAddress = allocator.TryAllocate(allocatedLength);
            if (logicalAddress == 0)
            {
                epoch.Suspend();
                return false;
            }

            var physicalAddress = allocator.GetPhysicalAddress(logicalAddress);
            for (int i = 0; i < totalEntries; i++)
            {
                var span = readOnlySpanBatch.Get(i);
                var entryLength = span.Length;
                *(int*)physicalAddress = entryLength;
                fixed (byte* bp = &span.GetPinnableReference())
                    Buffer.MemoryCopy(bp, (void*)(4 + physicalAddress), entryLength, entryLength);
                physicalAddress += Align(entryLength) + 4;
            }

            epoch.Suspend();
            return true;
        }

        private unsafe void AsyncGetFromDiskCallback(uint errorCode, uint numBytes, NativeOverlapped* overlap)
        {
            var ctx = (SimpleReadContext)Overlapped.Unpack(overlap).AsyncResult;

            if (errorCode != 0)
            {
                Trace.TraceError("OverlappedStream GetQueuedCompletionStatus error: {0}", errorCode);
                ctx.record.Return();
                ctx.record = null;
                ctx.completedRead.Release();
            }
            else
            {
                var record = ctx.record.GetValidPointer();
                var length = *(int*)record;

                if (length < 0 || length > allocator.PageSize)
                {
                    Debug.WriteLine("Invalid record length found: " + length);
                    ctx.record.Return();
                    ctx.record = null;
                    ctx.completedRead.Release();
                }
                else
                {
                    int requiredBytes = 4 + length;
                    if (ctx.record.available_bytes >= requiredBytes)
                    {
                        ctx.completedRead.Release();
                    }
                    else
                    {
                        ctx.record.Return();
                        allocator.AsyncReadRecordToMemory(ctx.logicalAddress, requiredBytes, AsyncGetFromDiskCallback, ref ctx);
                    }
                }
            }
            Overlapped.Free(overlap);
        }

        private (byte[], int) GetRecordAndFree(SectorAlignedMemory record)
        {
            if (record == null)
                return (null, 0);

            byte[] result;
            int length;
            unsafe
            {
                var ptr = record.GetValidPointer();
                length = *(int*)ptr;
                result = getMemory != null ? getMemory(length) : new byte[length];
                fixed (byte* bp = result)
                {
                    Buffer.MemoryCopy(ptr + 4, bp, length, length);
                }
            }
            record.Return();
            return (result, length);
        }

        private long CommitInternal(bool spinWait = false)
        {
            epoch.Resume();
            if (allocator.ShiftReadOnlyToTail(out long tailAddress))
            {
                if (spinWait)
                {
                    while (CommittedUntilAddress < tailAddress)
                    {
                        epoch.ProtectAndDrain();
                        Thread.Yield();
                    }
                }
                epoch.Suspend();
            }
            else
            {
                // May need to commit begin address
                epoch.Suspend();
                CommitCallback(CommittedUntilAddress);
            }

            return tailAddress;
        }
    }
}
