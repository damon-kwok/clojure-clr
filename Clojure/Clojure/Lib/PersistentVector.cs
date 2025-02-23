﻿/**
 *   Copyright (c) Rich Hickey. All rights reserved.
 *   The use and distribution terms for this software are covered by the
 *   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
 *   which can be found in the file epl-v10.html at the root of this distribution.
 *   By using this software in any fashion, you are agreeing to be bound by
 * 	 the terms of this license.
 *   You must not remove this notice, or any other, from this software.
 **/

/**
 *   Author: David Miller
 **/

using System;
using System.Collections;
using System.Threading;
using System.Collections.Generic;

namespace clojure.lang
{
    /// <summary>
    /// Implements a persistent vector using a specialized form of array-mapped hash trie.
    /// </summary>
    [Serializable]
    public class PersistentVector: APersistentVector, IObj, IEditableCollection, IEnumerable, IReduce, IKVReduce, IDrop
    {
        #region Node class

        [Serializable]
        public sealed class Node
        {
            #region Data

            [NonSerialized]
            readonly AtomicReference<Thread> _edit;

            public AtomicReference<Thread> Edit
            {
                get { return _edit; }
            } 

            readonly object[] _array;

            public object[] Array
            {
                get { return _array; }
            } 

            
            #endregion

            #region C-tors

            public Node(AtomicReference<Thread> edit, object[] array)
            {
                _edit = edit;
                _array = array;
            }

            public Node(AtomicReference<Thread> edit)
            {
                _edit = edit;
                _array = new object[32];
            }
        
            #endregion
        }

        #endregion

        #region Data

        static readonly AtomicReference<Thread> NoEdit = new AtomicReference<Thread>();
        internal static readonly Node EmptyNode = new Node(NoEdit, new object[32]);

        readonly int _cnt;
        readonly int _shift;
        readonly Node _root;
        readonly object[] _tail;

        public int Shift { get { return _shift; } }
        public Node Root { get { return _root; } }
        public object[] Tail() { return _tail; } 

        readonly IPersistentMap _meta;

        /// <summary>
        /// An empty <see cref="PersistentVector">PersistentVector</see>.
        /// </summary>
        static public readonly PersistentVector EMPTY = new PersistentVector(0,5,EmptyNode, new object[0]);

        #endregion

        #region Transient vector conj

        private sealed class TransientVectorConjer : AFn
        {
            public override object invoke(object coll, object val)
            {
                return ((ITransientVector)coll).conj(val);
            }

            public override object invoke(object coll)
            {
                return coll;
            }
        }

        static readonly IFn _transientVectorConj = new TransientVectorConjer();

        #endregion

        #region C-tors and factory methods

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        public static PersistentVector adopt(Object[] items)
        {
            return new PersistentVector(items.Length, 5, EmptyNode, items);
        }

        /// <summary>
        /// Create a <see cref="PersistentVector">PersistentVector</see> from an <see cref="ISeq">IReduceInit</see>.
        /// </summary>
        /// <param name="items"></param>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        static public PersistentVector create(IReduceInit items)
        {
            TransientVector ret = (TransientVector)EMPTY.asTransient();
            items.reduce(_transientVectorConj, ret);
            return (PersistentVector)ret.persistent();
        }

        /// <summary>
        /// Create a <see cref="PersistentVector">PersistentVector</see> from an <see cref="ISeq">ISeq</see>.
        /// </summary>
        /// <param name="items">A sequence of items.</param>
        /// <returns>An initialized vector.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        static public PersistentVector create(ISeq items)
        {
            Object[] arr = new Object[32];
            int i = 0;
            for (; items != null && i < 32; items = items.next())
                arr[i++] = items.first();

            if (items != null)
            {
                // >32, construct with array directly
                PersistentVector start = new PersistentVector(32, 5, EmptyNode, arr);
                TransientVector ret = (TransientVector)start.asTransient();
                for (; items != null; items = items.next())
                    ret = (TransientVector)ret.conj(items.first());
                return (PersistentVector)ret.persistent();
            }
            else if (i == 32)
            {
                // exactly 32, skip copy
                return new PersistentVector(32, 5, EmptyNode, arr);
            }
            else
            {
                // <32, copy to minimum array and construct
                Object[] arr2 = new Object[i];
                Array.Copy(arr, 0, arr2, 0, i);

                return new PersistentVector(i, 5, EmptyNode, arr2);
            }
        }

        /// <summary>
        /// Create a <see cref="PersistentVector">PersistentVector</see> from an array of items.
        /// </summary>
        /// <param name="items"></param>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        static public PersistentVector create(params object[] items)
        {
            ITransientCollection ret = EMPTY.asTransient();
            foreach (object item in items)
                ret = ret.conj(item);
            return (PersistentVector)ret.persistent();
        }

        /// <summary>
        /// Create a <see cref="PersistentVector">PersistentVector</see> from an IEnumerable.
        /// </summary>
        /// <param name="items"></param>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        static public PersistentVector create1(IEnumerable items)
        {
            // optimize common case
            if (items is IList ilist)
            {
                int size = ilist.Count;
                if (size <= 32)
                {
                    Object[] arr = new Object[size];
                    ilist.CopyTo(arr, 0);

                    return new PersistentVector(size, 5, PersistentVector.EmptyNode, arr);
                }
            }

            ITransientCollection ret = EMPTY.asTransient();
            foreach (object item in items)
            {
                ret = ret.conj(item);
            }
            return (PersistentVector)ret.persistent();
        }


        /// <summary>
        /// Initialize a <see cref="PersistentVector">PersistentVector</see> from basic components.
        /// </summary>
        /// <param name="cnt"></param>
        /// <param name="shift"></param>
        /// <param name="root"></param>
        /// <param name="tail"></param>
        public PersistentVector(int cnt, int shift, Node root, object[] tail)
        {
            _meta = null;
            _cnt = cnt;
            _shift = shift;
            _root = root;
            _tail = tail;
        }


        /// <summary>
        /// Initialize a <see cref="PersistentVector">PersistentVector</see> from given metadata and basic components.
        /// </summary>
        /// <param name="meta"></param>
        /// <param name="cnt"></param>
        /// <param name="shift"></param>
        /// <param name="root"></param>
        /// <param name="tail"></param>
        PersistentVector(IPersistentMap meta, int cnt, int shift, Node root, object[] tail)
        {
            _meta = meta;
            _cnt = cnt;
            _shift = shift;
            _root = root;
            _tail = tail;
        }

        #endregion

        #region IObj members

        public IObj withMeta(IPersistentMap meta)
        {
            if (_meta == meta)
                return this;

            return new PersistentVector(meta, _cnt, _shift, _root, _tail);
        }

        #endregion

        #region IMeta Members

        public IPersistentMap meta()
        {
            return _meta;
        }

        #endregion

        #region IPersistentVector members

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        int tailoff()
        {
            if (_cnt < 32)
                return 0;
            return ((_cnt - 1) >> 5) << 5;
        }


        /// <summary>
        /// Get the i-th item in the vector.
        /// </summary>
        /// <param name="i">The index of the item to retrieve/</param>
        /// <returns>The i-th item</returns>
        /// <remarks>Throws an exception if the index <c>i</c> is not in the range of the vector's elements.</remarks>
        public override object nth(int i)
        {
            object[] node = ArrayFor(i);
            return node[i & 0x01f];
        }

        public override Object nth(int i, Object notFound)
        {
            if (i >= 0 && i < _cnt)
                return nth(i);
            return notFound;
        }

        object[] ArrayFor(int i) 
        {
            if (i >= 0 && i < _cnt)
            {
                if (i >= tailoff())
                    return _tail;
                Node node = _root;
                for (int level = _shift; level > 0; level -= 5)
                    node = (Node)node.Array[(i >> level) & 0x01f];
                return node.Array;
            }
            throw new ArgumentOutOfRangeException("i");
        }


        /// <summary>
        /// Return a new vector with the i-th value set to <c>val</c>.
        /// </summary>
        /// <param name="i">The index of the item to set.</param>
        /// <param name="val">The new value</param>
        /// <returns>A new (immutable) vector v with v[i] == val.</returns>
        public override IPersistentVector assocN(int i, Object val)
        {
            if (i >= 0 && i < _cnt)
            {
                if (i >= tailoff())
                {
                    object[] newTail = new object[_tail.Length];
                    Array.Copy(_tail, newTail, _tail.Length);
                    newTail[i & 0x01f] = val;

                    return new PersistentVector(meta(), _cnt, _shift, _root, newTail);
                }

                return new PersistentVector(meta(), _cnt, _shift, doAssoc(_shift, _root, i, val), _tail);
            }
            if (i == _cnt)
                return cons(val);
            throw new ArgumentOutOfRangeException("i");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        static private Node doAssoc(int level, Node node, int i, object val)
        {
            Node ret = new Node(node.Edit, (object[])node.Array.Clone());
            if (level == 0)
                ret.Array[i & 0x01f] = val;
            else
            {
                int subidx = ( i >> level ) & 0x01f;
                ret.Array[subidx] = doAssoc(level-5,(Node) node.Array[subidx], i, val);
            }
            return ret;
        }

        /// <summary>
        /// Creates a new vector with a new item at the end.
        /// </summary>
        /// <param name="o">The item to add to the vector.</param>
        /// <returns>A new (immutable) vector with the objected added at the end.</returns>
        /// <remarks>Overrides <c>cons</c> in <see cref="IPersistentCollection">IPersistentCollection</see> to specialize the return value.</remarks>

        public override IPersistentVector cons(object o)
        {
            //if (_tail.Length < 32)
            if ( _cnt - tailoff() < 32 )
            {
                object[] newTail = new object[_tail.Length + 1];
                Array.Copy(_tail, newTail, _tail.Length);
                newTail[_tail.Length] = o;
                return new PersistentVector(meta(), _cnt + 1, _shift, _root, newTail);
            }

            // full tail, push into tree
            Node newroot;
            Node tailnode = new Node(_root.Edit, _tail);
            int newshift = _shift;
            
            // overflow root?
            if ((_cnt >> 5) > (1 << _shift))
            {
                newroot = new Node(_root.Edit);
                newroot.Array[0] = _root;
                newroot.Array[1] = newPath(_root.Edit, _shift, tailnode);
                newshift += 5;
            }
            else
                newroot = pushTail(_shift, _root, tailnode);


            return new PersistentVector(meta(), _cnt + 1, newshift, newroot, new object[] { o });
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        private Node pushTail(int level, Node parent, Node tailnode)
        {
            // if parent is leaf, insert node,
            // else does it map to existing child?  -> nodeToInsert = pushNode one more level
            // else alloc new path
            // return nodeToInsert placed in copy of parent
            int subidx = ((_cnt - 1) >> level) & 0x01f;
            Node ret = new Node(parent.Edit, (object[])parent.Array.Clone());
            Node nodeToInsert;

            if (level == 5)
                nodeToInsert = tailnode;
            else
            {
                Node child = (Node)parent.Array[subidx];
                nodeToInsert = (child != null
                                 ? pushTail(level - 5, child, tailnode)
                                 : newPath(_root.Edit, level - 5, tailnode));
            }
            ret.Array[subidx] = nodeToInsert;
            return ret;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        static Node newPath(AtomicReference<Thread> edit, int level, Node node)
        {
            if (level == 0)
                return node;

            Node ret = new Node(edit);
            ret.Array[0] = newPath(edit, level - 5, node);
            return ret;
        }

        #endregion

        #region IPersistentCollection members

        /// <summary>
        /// Gets the number of items in the collection.
        /// </summary>
        /// <returns>The number of items in the collection.</returns>
        public override int count()
        {
            return _cnt;
        }

        /// <summary>
        /// Gets an empty collection of the same type.
        /// </summary>
        /// <returns>An emtpy collection.</returns>
        public override IPersistentCollection empty()
        {
            return (IPersistentCollection)EMPTY.withMeta(meta());
        }
        
        #endregion

        #region IPersistentStack members

        /// <summary>
        /// Returns a new stack with the top element popped.
        /// </summary>
        /// <returns>The new stack.</returns>
        public override IPersistentStack pop()
        {
            if ( _cnt == 0 )
                throw new InvalidOperationException("Can't pop empty vector");
            if ( _cnt == 1)
                return (IPersistentStack)EMPTY.withMeta(meta());
            //if ( _tail.Length > 1 )
            if (_cnt - tailoff() > 1)
            {
                object[] newTail = new object[_tail.Length-1];
                Array.Copy(_tail,newTail,newTail.Length);
                return new PersistentVector(meta(),_cnt-1,_shift,_root,newTail);
            }
            object[] newtail = ArrayFor(_cnt - 2);

            Node newroot = popTail(_shift,_root);
            int newshift = _shift;
            if ( newroot == null )
                newroot = EmptyNode;
            if ( _shift > 5 && newroot.Array[1] == null )
            {
                newroot = (Node)newroot.Array[0];
                newshift -= 5;
            }
            return new PersistentVector(meta(), _cnt - 1, newshift, newroot, newtail);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        private Node popTail(int level, Node node)
        {
            int subidx = ((_cnt - 2) >> level) & 0x01f;
            if (level > 5)
            {
                Node newchild = popTail(level - 5, (Node)node.Array[subidx]);
                if (newchild == null && subidx == 0)
                    return null;
                else
                {
                    Node ret = new Node(_root.Edit, (object[])node.Array.Clone());
                    ret.Array[subidx] = newchild;
                    return ret;
                }
            }
            else if (subidx == 0)
                return null;
            else
            {
                Node ret = new Node(_root.Edit, (object[])node.Array.Clone());
                ret.Array[subidx] = null;
                return ret;
            }
        }


        #endregion

        #region IFn members



        #endregion

        #region Seqable members

        public override ISeq seq()
        {
            return chunkedSeq();
        }

        #endregion

        #region ChunkedSeq

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
        public IChunkedSeq chunkedSeq()
        {
            if (count() == 0)
                return null;
            return new ChunkedSeq(this, 0, 0);
        }

        [Serializable]
        sealed public class ChunkedSeq : ASeq, IChunkedSeq, Counted, IReduce, IDrop, IEnumerable
        {
            #region Data

            readonly PersistentVector _vec;
            readonly object[] _node;
            readonly int _i;
            readonly int _offset;

            public int Offset
            {
                get { return _offset; }
            }

            public PersistentVector Vec
            {
                get { return _vec; }
            }

            #endregion

            #region C-tors

            public ChunkedSeq(PersistentVector vec, int i, int offset)
            {
                _vec = vec;
                _i = i;
                _offset = offset;
                _node = vec.ArrayFor(i);
            }

            ChunkedSeq(IPersistentMap meta, PersistentVector vec, object[] node, int i, int offset)
                : base(meta)
            {
                _vec = vec;
                _node = node;
                _i = i;
                _offset = offset;
            }

            public ChunkedSeq(PersistentVector vec, object[] node, int i, int offset)
            {
                _vec = vec;
                _node = node;
                _i = i;
                _offset = offset;
            }

            #endregion

            #region IObj members


            public override IObj withMeta(IPersistentMap meta)
            {
                return (meta == _meta)
                    ? this
                    : new ChunkedSeq(meta, _vec, _node, _i, _offset);
            }

            #endregion

            #region IChunkedSeq Members

            public IChunk chunkedFirst()
            {
                return new ArrayChunk(_node, _offset);
            }

            public ISeq chunkedNext()
            {
                if (_i + _node.Length < _vec._cnt)
                    return new ChunkedSeq(_vec, _i + _node.Length, 0);
                return null;
            }

            public ISeq chunkedMore()
            {
                ISeq s = chunkedNext();
                if (s == null)
                    return PersistentList.EMPTY;
                return s;
            }

            #endregion

            #region IPersistentCollection Members


            //public new IPersistentCollection cons(object o)
            //{
            //    throw new NotImplementedException();
            //}

            #endregion

            #region ISeq members

            public override object first()
            {
                return _node[_offset];
            }

            public override ISeq next()
            {
                if (_offset + 1 < _node.Length)
                    return new ChunkedSeq(_vec, _node, _i, _offset + 1);
                return chunkedNext();
            }

            #endregion

            #region Counted members

            public override int count()
            {
                return _vec._cnt - (_i + _offset);
            }

            #endregion

            #region IReduce members

            public object reduce(IFn f)
            {
                object acc;
                if (_i + _offset < _vec._cnt)
                    acc = _node[_offset];
                else
                    return f.invoke();

                for (int j = _offset + 1; j < _node.Length; j++)
                {
                    acc = f.invoke(acc, _node[j]);
                    if (RT.isReduced(acc))
                        return ((IDeref)acc).deref();
                }

                int step = 0;
                for (int ii = _i + _node.Length; ii < _vec._cnt; ii += step)
                {
                    object[] array = _vec.ArrayFor(ii);
                    for (int j = 0; j < array.Length; j++)
                    {
                        acc = f.invoke(acc, array[j]);
                        if (RT.isReduced(acc))
                            return ((IDeref)acc).deref();
                    }
                    step = array.Length;
                }

                return acc;
            }
       

            public object reduce(IFn f, object start)
            {
                object acc = start;

                for (int j = _offset; j < _node.Length; j++)
                {
                    acc = f.invoke(acc, _node[j]);
                    if (RT.isReduced(acc))
                        return ((IDeref)acc).deref();
                }

                int step = 0;
                for (int ii = _i + _node.Length; ii < _vec._cnt; ii += step)
                {
                    object[] array = _vec.ArrayFor(ii);
                    for (int j = 0; j < array.Length; j++)
                    {
                        acc = f.invoke(acc, array[j]);
                        if (RT.isReduced(acc))
                            return ((IDeref)acc).deref();
                    }
                    step = array.Length;
                }

                return acc;
            }

            #endregion

            #region IDrop members

            public Sequential drop(int n)
            {
                int o = _offset + n;
                if (o < _node.Length)   // in current array
                    return new ChunkedSeq(_vec, _node, _i, o);
                else
                {
                    int i = _i + o;
                    if (i < _vec._cnt) // in vec
                    {
                        var array = _vec.ArrayFor(i);
                        int newOffset = i % 32;
                        return new ChunkedSeq(_vec, array, i - newOffset, newOffset);
                    }
                    else
                        return null;
                }
            }

            #endregion

            #region IEnumerable

            public override IEnumerator<object> GetEnumerator()
            {
                return _vec.RangedIteratorT(_i+_offset,_vec._cnt );
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            #endregion


        }

        #endregion

        #region IEditableCollection Members

        public ITransientCollection asTransient()
        {
            return new TransientVector(this);
        }

        #endregion

        #region TransientVector class

        class TransientVector : AFn, ITransientVector, ITransientAssociative2, Counted
        {
            #region Data

            volatile int _cnt;
            volatile int _shift;
            volatile Node _root;
            volatile object[] _tail;

            #endregion

            #region Ctors

            TransientVector(int cnt, int shift, Node root, Object[] tail)
            {
                _cnt = cnt;
                _shift = shift;
                _root = root;
                _tail = tail;
            }

            public TransientVector(PersistentVector v)
                : this(v._cnt, v._shift, EditableRoot(v._root), EditableTail(v._tail))
            {
            }

            #endregion

            #region Counted Members

            public int count()
            {
                EnsureEditable();
                return _cnt;
            }

            #endregion

            #region Implementation

            void EnsureEditable()
            {
                Thread owner = _root.Edit.Get();
                if (owner == null)
                    throw new InvalidOperationException("Transient used after persistent! call");
            }


            Node EnsureEditable(Node node)
            {
                if (node.Edit == _root.Edit)
                    return node;
                return new Node(_root.Edit, (object[])node.Array.Clone());
            }

            static Node EditableRoot(Node node)
            {
                return new Node(new AtomicReference<Thread>(Thread.CurrentThread), (object[])node.Array.Clone());
            }

            static object[] EditableTail(object[] tl)
            {
                object[] ret = new object[32];
                Array.Copy(tl, ret, tl.Length);
                return ret;
            }

            Node PushTail(int level, Node parent, Node tailnode)
            {
                //if parent is leaf, insert node,
                // else does it map to an existing child? -> nodeToInsert = pushNode one more level
                // else alloc new path
                //return  nodeToInsert placed in copy of parent
                int subidx = ((_cnt - 1) >> level) & 0x01f;
                Node ret = new Node(parent.Edit, (object[])parent.Array.Clone());
                Node nodeToInsert;
                if (level == 5)
                {
                    nodeToInsert = tailnode;
                }
                else
                {
                    Node child = (Node)parent.Array[subidx];
                    nodeToInsert = (child != null)
                        ? PushTail(level - 5, child, tailnode)
                                                   : newPath(_root.Edit, level - 5, tailnode);
                }
                ret.Array[subidx] = nodeToInsert;
                return ret;
            }

            int Tailoff()
            {
                if (_cnt < 32)
                    return 0;
                return ((_cnt - 1) >> 5) << 5;
            }

            object[] ArrayFor(int i)
            {
                if (i >= 0 && i < _cnt)
                {
                    if (i >= Tailoff())
                        return _tail;
                    Node node = _root;
                    for (int level = _shift; level > 0; level -= 5)
                        node = (Node)node.Array[(i >> level) & 0x01f];
                    return node.Array;
                }
                throw new ArgumentOutOfRangeException("i");
            }

            object[] EditableArrayFor(int i)
            {
                if (i >= 0 && i < _cnt)
                {
                    if (i >= Tailoff())
                        return _tail;
                    Node node = _root;
                    for (int level = _shift; level > 0; level -= 5)
                        node = EnsureEditable((Node)node.Array[(i >> level) & 0x01f]);
                    return node.Array;
                }
                throw new ArgumentOutOfRangeException("i");
            }

            #endregion

            #region ITransientVector Members

            public object nth(int i)
            {
                object[] node = ArrayFor(i);
                return node[i & 0x01f];
            }


            public object nth(int i, object notFound)
            {
                if (i >= 0 && i < count())
                    return nth(i);
                return notFound;
            }

            public ITransientVector assocN(int i, object val)
            {
                EnsureEditable();
                if (i >= 0 && i < _cnt)
                {
                    if (i >= Tailoff())
                    {
                        _tail[i & 0x01f] = val;
                        return this;
                    }

                    _root = doAssoc(_shift, _root, i, val);
                    return this;
                }
                if (i == _cnt)
                    return (ITransientVector)conj(val);
                throw new ArgumentOutOfRangeException("i");
            }

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "ClojureJVM name match")]
            Node doAssoc(int level, Node node, int i, Object val)
            {
                node = EnsureEditable(node);
                Node ret = new Node(node.Edit, (object[])node.Array.Clone());
                if (level == 0)
                {
                    ret.Array[i & 0x01f] = val;
                }
                else
                {
                    int subidx = (i >> level) & 0x01f;
                    ret.Array[subidx] = doAssoc(level - 5, (Node)node.Array[subidx], i, val);
                }
                return ret;
            }

            public ITransientVector pop()
            {
                EnsureEditable();
                if (_cnt == 0)
                    throw new InvalidOperationException("Can't pop empty vector");
                if (_cnt == 1)
                {
                    _cnt = 0;
                    return this;
                }
                int i = _cnt - 1;
                // pop in tail?
                if ((i & 0x01f) > 0)
                {
                    --_cnt;
                    return this;
                }
                object[] newtail = EditableArrayFor(_cnt - 2);

                Node newroot = PopTail(_shift, _root);
                int newshift = _shift;
                if (newroot == null)
                {
                    newroot = new Node(_root.Edit);
                }
                if (_shift > 5 && newroot.Array[1] == null)
                {
                    newroot = EnsureEditable((Node)newroot.Array[0]);
                    newshift -= 5;
                }
                _root = newroot;
                _shift = newshift;
                --_cnt;
                _tail = newtail;
                return this;
            }


            private Node PopTail(int level, Node node)
            {
                node = EnsureEditable(node);
                int subidx = ((_cnt - 2) >> level) & 0x01f;
                if (level > 5)
                {
                    Node newchild = PopTail(level - 5, (Node)node.Array[subidx]);
                    if (newchild == null && subidx == 0)
                        return null;
                    else
                    {
                        Node ret = node;
                        ret.Array[subidx] = newchild;
                        return ret;
                    }
                }
                else if (subidx == 0)
                    return null;
                else
                {
                    Node ret = node;
                    ret.Array[subidx] = null;
                    return ret;
                }
            }

            #endregion

            #region ITransientAssociative Members

            public ITransientAssociative assoc(object key, object val)
            {
                if (Util.IsIntegerType(key.GetType()))
                {
                    int i = Util.ConvertToInt(key);
                    return assocN(i, val);
                }
                throw new ArgumentException("Key must be integer");
            }

            #endregion

            #region ITransientCollection Members

            public ITransientCollection conj(object val)
            {

                EnsureEditable();
                int i = _cnt;
                //room in tail?
                if (i - Tailoff() < 32)
                {
                    _tail[i & 0x01f] = val;
                    ++_cnt;
                    return this;
                }
                //full tail, push into tree
                Node newroot;
                Node tailnode = new Node(_root.Edit, _tail);
                _tail = new object[32];
                _tail[0] = val;
                int newshift = _shift;
                //overflow root?
                if ((_cnt >> 5) > (1 << _shift))
                {
                    newroot = new Node(_root.Edit);
                    newroot.Array[0] = _root;
                    newroot.Array[1] = newPath(_root.Edit, _shift, tailnode);
                    newshift += 5;
                }
                else
                    newroot = PushTail(_shift, _root, tailnode);
                _root = newroot;
                _shift = newshift;
                ++_cnt;
                return this;
            }

            public IPersistentCollection persistent()
            {
                EnsureEditable();
                _root.Edit.Set(null);
                object[] trimmedTail = new object[_cnt-Tailoff()];
                Array.Copy(_tail,trimmedTail,trimmedTail.Length);
                return new PersistentVector(_cnt, _shift, _root, trimmedTail);
            }

            #endregion

            #region ITransientAssociative2 methods

            private static readonly Object NOT_FOUND = new object();

            public bool containsKey(object key)
            {
                return valAt(key, NOT_FOUND) != NOT_FOUND;
            }

            public IMapEntry entryAt(object key)
            {
                Object v = valAt(key, NOT_FOUND);
                if (v != NOT_FOUND)
                    return MapEntry.create(key, v);
                return null;
            }

            #endregion


            #region ILookup Members


            public object valAt(object key)
            {
                // note - relies on EnsureEditable in 2-arg valAt
                return valAt(key, null);
            }

            public object valAt(object key, object notFound)
            {
                EnsureEditable();
                if (Util.IsIntegerType(key.GetType()))
                {
                    int i = Util.ConvertToInt(key);
                    if (i >= 0 && i < count())
                        return nth(i);
                }
                return notFound;
            }

            #endregion
        }
 
        #endregion

        #region IReduce members and kvreduce

        public object reduce(IFn f)
        {
            Object init;
            if (_cnt > 0)
                init = ArrayFor(0)[0];
            else
                return f.invoke();
            int step;
            for (int i = 0; i < _cnt; i += step)
            {
                Object[] array = ArrayFor(i);
                for (int j = (i == 0) ? 1 : 0; j < array.Length; ++j)
                {
                    init = f.invoke(init, array[j]);
                    if (RT.isReduced(init))
                        return ((IDeref)init).deref();
                }
                step = array.Length;
            }
            return init;
        }

        public object reduce(IFn f, object start)
        {
            int step;
            for (int i = 0; i < _cnt; i += step)
            {
                Object[] array = ArrayFor(i);
                for (int j = 0; j < array.Length; ++j)
                {
                    start = f.invoke(start, array[j]);
                    if (RT.isReduced(start))
                        return ((IDeref)start).deref();
                }
                step = array.Length;
            }
            return start;
        }

        public object kvreduce(IFn f, object init)
        {
            int step;
            for (int i = 0; i < _cnt; i += step)
            {
                object[] array = ArrayFor(i);
                for (int j = 0; j < array.Length; j++)
                {
                    init = f.invoke(init, j + i, array[j]);
                    if (RT.isReduced(init))
                        return ((IDeref)init).deref();
                }
                step = array.Length;
            }
            return init;
        }

        #endregion

        #region IDrop members

        public Sequential drop(int n)
        {
            if (n < 0)
                return this;
            else if (n < _cnt)
            {
                int offset = n % 32;
                return new ChunkedSeq(this, this.ArrayFor(n), n - offset, offset);
            }
            else
                return null;
        }

        #endregion

        #region Ranged iterator

        public override IEnumerator RangedIterator(int start, int end)
        {
            int i = start;
            int b = i - (i%32);
            object[] arr = (start < count()) ? ArrayFor(i) : null;

            while (i < end)
            {
                if (i - b == 32)
                {
                    arr = ArrayFor(i);
                    b += 32;
                }
                yield return arr[i++ & 0x01f];
            }
        }

        public override IEnumerator<object> RangedIteratorT(int start, int end)
        {
            int i = start;
            int b = i - (i % 32);
            object[] arr = (start < count()) ? ArrayFor(i) : null;

            while (i < end)
            {
                if (i - b == 32)
                {
                    arr = ArrayFor(i);
                    b += 32;
                }
                yield return arr[i++ & 0x01f];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return RangedIterator(0, count());
        }
        
        public override IEnumerator<object> GetEnumerator()
        {
            return RangedIteratorT(0, count());

        }

        #endregion
    }
}
