/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes
{
    public class PriorityQueue
    {
//        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public delegate bool UpdatePriorityHandler(ref uint priority, ISceneEntity entity);

        /// <summary>
        /// Total number of queues (priorities) available
        /// </summary>

        public const uint NumberOfQueues = 13; // includes immediate queues, m_queueCounts need to be set acording

        /// <summary>
        /// Number of queuest (priorities) that are processed immediately
        /// </summary.
        public const uint NumberOfImmediateQueues = 2;
        // first queues are immediate, so no counts
        private static readonly uint[] m_queueCounts = {0, 0, 8, 8, 5, 4, 3, 2, 1, 1, 1, 1, 1 };
        // this is                     ava, ava, attach, <10m, 20,40,80,160m,320,640,1280, +

        private MinHeap<EntityUpdate>[] m_heaps = new MinHeap<EntityUpdate>[NumberOfQueues];
        private Dictionary<uint, LookupItem> m_lookupTable;

        // internal state used to ensure the deqeues are spread across the priority
        // queues "fairly". queuecounts is the amount to pull from each queue in
        // each pass. weighted towards the higher priority queues
        private uint m_nextQueue = 0;
        private uint m_countFromQueue = 0;
        private int m_capacity;
        private int m_added;

        // next request is a counter of the number of updates queued, it provides
        // a total ordering on the updates coming through the queue and is more
        // lightweight (and more discriminating) than tick count
        private ulong m_nextRequest = 0;

        /// <summary>
        /// Lock for enqueue and dequeue operations on the priority queue
        /// </summary>
        private object m_syncRoot = new object();
        public object SyncRoot {
            get { return this.m_syncRoot; }
        }

#region constructor
        public PriorityQueue() : this(MinHeap<EntityUpdate>.DEFAULT_CAPACITY) { }

        public PriorityQueue(int capacity)
        {
            m_capacity = 16;
            capacity /= 4;

            for (int i = 0; i < m_heaps.Length; ++i)
                m_heaps[i] = new MinHeap<EntityUpdate>(capacity);

            m_lookupTable = new Dictionary<uint, LookupItem>(m_capacity);
            m_nextQueue = NumberOfImmediateQueues;
            m_countFromQueue = m_queueCounts[m_nextQueue];
            m_added = 0;
        }
#endregion Constructor

#region PublicMethods
        public void Close()
        {
            for (int i = 0; i < m_heaps.Length; ++i)
            {
                foreach(EntityUpdate eu in m_heaps[i])
                    eu.Free();
                m_heaps[i] = null;
            }

            m_heaps = null;
            m_lookupTable.Clear();
            m_lookupTable = null;
        }

        /// <summary>
        /// Return the number of items in the queues
        /// </summary>
        public int Count
        {
            get
            {
                int count = 0;
                for (int i = 0; i < m_heaps.Length; ++i)
                    count += m_heaps[i].Count;

                return count;
            }
        }

        /// <summary>
        /// Enqueue an item into the specified priority queue
        /// </summary>
        public bool Enqueue(uint pqueue, EntityUpdate value)
        {
            LookupItem lookup;
            IHandle lookupH; 
            ulong entry;

            uint localid = value.Entity.LocalId;
            if (m_lookupTable.TryGetValue(localid, out lookup))
            {
                lookupH = lookup.Handle;
                entry = lookup.Heap[lookupH].EntryOrder;
                EntityUpdate up = lookup.Heap[lookupH];
                lookup.Heap.Remove(lookupH);

                if((up.Flags & PrimUpdateFlags.CancelKill) != 0)
                    entry = m_nextRequest++;

                pqueue = Util.Clamp<uint>(pqueue, 0, NumberOfQueues - 1);
                value.Update(up, pqueue, entry);
                up.Free();

                lookup.Heap = m_heaps[pqueue];
                lookup.Heap.Add(value, ref lookup.Handle);
                m_lookupTable[localid] = lookup;
                return true;
            }

            entry = m_nextRequest++;
            ++m_added;
            pqueue = Util.Clamp<uint>(pqueue, 0, NumberOfQueues - 1);
            value.Update(pqueue, entry);

            lookup.Heap = m_heaps[pqueue];
            lookup.Heap.Add(value, ref lookup.Handle);
            m_lookupTable[localid] = lookup;

            return true;
        }

        public void Remove(List<uint> ids)
        {
            LookupItem lookup;

            foreach (uint localid in ids)
            {
                if (m_lookupTable.TryGetValue(localid, out lookup))
                {
                    lookup.Heap[lookup.Handle].Free();
                    lookup.Heap.Remove(lookup.Handle);
                    m_lookupTable.Remove(localid);
                }
            }
            if(m_lookupTable.Count == 0 && m_added > 8 * m_capacity)
            {
                m_lookupTable = new Dictionary<uint, LookupItem>(m_capacity);
                m_added = 0;
            }
        }

        /// <summary>
        /// Remove an item from one of the queues. Specifically, it removes the
        /// oldest item from the next queue in order to provide fair access to
        /// all of the queues
        /// </summary>
        public bool TryDequeue(out EntityUpdate value)
        {
            // If there is anything in immediate queues, return it first no
            // matter what else. Breaks fairness. But very useful.
            
            for (int iq = 0; iq < NumberOfImmediateQueues; iq++)
            {
                if (m_heaps[iq].Count > 0)
                {
                    value = m_heaps[iq].RemoveMin();
                    m_lookupTable.Remove(value.Entity.LocalId);
                    return true;
                }
            }

            // To get the fair queing, we cycle through each of the
            // queues when finding an element to dequeue.
            // We pull (NumberOfQueues - QueueIndex) items from each queue in order
            // to give lower numbered queues a higher priority and higher percentage
            // of the bandwidth.

            MinHeap<EntityUpdate> curheap = m_heaps[m_nextQueue];
            // Check for more items to be pulled from the current queue
            if (m_countFromQueue > 0 && curheap.Count > 0)
            {
                --m_countFromQueue;

                value = curheap.RemoveMin();
                m_lookupTable.Remove(value.Entity.LocalId);
                return true;
            }

            // Find the next non-immediate queue with updates in it
            for (uint i = NumberOfImmediateQueues; i < NumberOfQueues; ++i)
            {
                m_nextQueue++;
                if(m_nextQueue >= NumberOfQueues)
                    m_nextQueue = NumberOfImmediateQueues;
 
                curheap = m_heaps[m_nextQueue];
                if (curheap.Count == 0)
                    continue;

                m_countFromQueue = m_queueCounts[m_nextQueue];
                --m_countFromQueue;

                value = curheap.RemoveMin();
                m_lookupTable.Remove(value.Entity.LocalId);
                return true;
            }

            value = default(EntityUpdate);
            if(m_lookupTable.Count == 0 && m_added > 8 * m_capacity)
            {
                m_lookupTable = new Dictionary<uint, LookupItem>(m_capacity);
                m_added = 0;
            }
            return false;
        }

        public bool TryOrderedDequeue(out EntityUpdate value)
        {
            for (int iq = 0; iq < NumberOfQueues; ++iq)
            {
                MinHeap<EntityUpdate> curheap = m_heaps[iq];
                if (curheap.Count > 0)
                {
                    value = curheap.RemoveMin();
                    m_lookupTable.Remove(value.Entity.LocalId);
                    return true;
                }
            }

            value = default(EntityUpdate);
            if(m_lookupTable.Count == 0 && m_added > 8 * m_capacity)
            {
                m_lookupTable = new Dictionary<uint, LookupItem>(m_capacity);
                m_added = 0;
            }
            return false;
        }

        /// <summary>
        /// Reapply the prioritization function to each of the updates currently
        /// stored in the priority queues.
        /// </summary
        public void Reprioritize(UpdatePriorityHandler handler)
        {
            EntityUpdate currentEU;
            uint pqueue = 0;
            foreach (LookupItem lookup in new List<LookupItem>(m_lookupTable.Values))
            {
                if (lookup.Heap.TryGetValue(lookup.Handle, out currentEU))
                {
                    if (handler(ref pqueue, currentEU.Entity))
                    {
                        // unless the priority queue has changed, there is no need to modify
                        // the entry
                        pqueue = Util.Clamp<uint>(pqueue, 0, NumberOfQueues - 1);
                        if (pqueue != currentEU.PriorityQueue)
                        {
                            currentEU.PriorityQueue = pqueue;

                            lookup.Heap.Remove(lookup.Handle);
                            LookupItem litem = lookup;
                            litem.Heap = m_heaps[pqueue];
                            litem.Heap.Add(currentEU, ref litem.Handle);
                            m_lookupTable[currentEU.Entity.LocalId] = litem;
                        }
                    }
                    else
                    {
                        // m_log.WarnFormat("[PQUEUE]: UpdatePriorityHandler returned false for {0}",item.Value.Entity.UUID);
                        lookup.Heap.Remove(lookup.Handle);
                        m_lookupTable.Remove(currentEU.Entity.LocalId);
                    }
                }
            }
        }

        /// <summary>
        /// </summary>
        public override string ToString()
        {
            string s = "";
            for (int i = 0; i < NumberOfQueues; i++)
                s += String.Format("{0,7} ", m_heaps[i].Count);
            return s;
        }

#endregion PublicMethods


#region LookupItem
        private struct LookupItem
        {
            internal MinHeap<EntityUpdate> Heap;
            internal IHandle Handle;
        }
#endregion
    }
}