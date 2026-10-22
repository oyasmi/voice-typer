using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceTyper.Asr.Hotword;

/// <summary>
/// 热词上下文图（VOC-4）：所有热词 token 序列建成一棵前缀树，再补 Aho–Corasick 失配指针。构建后不可变，
/// 可跨线程共享。C# 直译自 <c>macos/Sources/VoiceTyper/ASR/Hotword/ContextGraph.swift</c>。
/// <list type="bullet">
/// <item>正在匹配的热词每多对上一个 token，排名分 +b（Dmax 封顶），让它能在束里活到匹配完成；</item>
/// <item>中途失配，暂存分全部撤回：只对上前缀的路径拿不到任何净收益；</item>
/// <item>完整匹配后锁定为 committed；<c>张三</c> 锁定后仍可继续走到 <c>张三丰</c>。</item>
/// </list>
/// 已知限制：一个热词是另一个热词的中缀或后缀、且二者重叠出现时，重叠部分可能被重复计分或漏计。
/// </summary>
internal sealed class ContextGraph
{
    /// <summary>假设携带的偏置状态。它是 token 序列的确定性函数，同一前缀从不同对齐到达时必然相同。</summary>
    internal readonly record struct State(int Node, double Committed, double Anchor, int Hits);

    public static readonly State Root = new(0, 0, 0, 0);

    /// <summary><c>(节点 &lt;&lt; 32) | token</c> → 子节点。</summary>
    private readonly Dictionary<long, int> _edges = new();
    private readonly int[] _fail;
    /// <summary><c>S(node) = b × min(depth, Dmax)</c>。</summary>
    private readonly double[] _score;
    private readonly bool[] _isEnd;
    /// <summary>每个节点的子节点 token，升序。</summary>
    private readonly int[][] _childTokens;

    /// <summary>所有热词序列的首 token，升序、去重。</summary>
    public IReadOnlyList<int> RootTokens => _childTokens[0];
    public int NodeCount => _isEnd.Length;

    public ContextGraph(IEnumerable<int[]> sequences, double perTokenBias, int maxScoredDepth)
    {
        var depth = new List<int> { 0 };
        var isEnd = new List<bool> { false };
        var childTokens = new List<List<int>> { new() };
        foreach (var sequence in sequences)
        {
            if (sequence.Length == 0) continue;
            int node = 0;
            foreach (var token in sequence)
            {
                var key = EdgeKey(node, token);
                if (_edges.TryGetValue(key, out var next))
                {
                    node = next;
                }
                else
                {
                    next = isEnd.Count;
                    _edges[key] = next;
                    depth.Add(depth[node] + 1);
                    isEnd.Add(false);
                    childTokens.Add(new List<int>());
                    childTokens[node].Add(token);
                    node = next;
                }
            }
            isEnd[node] = true;
        }
        _childTokens = childTokens.Select(list => list.OrderBy(t => t).ToArray()).ToArray();

        // BFS 补失配指针：fail(v) 指向「v 的路径的最长真后缀」在树中的节点。
        var fail = new int[isEnd.Count];
        var queue = new Queue<int>(_childTokens[0].Select(t => _edges[EdgeKey(0, t)]));
        while (queue.Count > 0)
        {
            int parent = queue.Dequeue();
            foreach (var token in _childTokens[parent])
            {
                int child = _edges[EdgeKey(parent, token)];
                int candidate = fail[parent];
                while (candidate != 0 && !_edges.ContainsKey(EdgeKey(candidate, token))) candidate = fail[candidate];
                fail[child] = _edges.TryGetValue(EdgeKey(candidate, token), out var target) ? target : 0;
                queue.Enqueue(child);
            }
        }

        _fail = fail;
        _isEnd = isEnd.ToArray();
        _score = depth.Select(d => perTokenBias * Math.Min(d, maxScoredDepth)).ToArray();
    }

    private static long EdgeKey(int node, int token) => ((long)node << 32) | (uint)token;

    /// <summary>正在匹配的热词的下一个 token（升序）。</summary>
    public IReadOnlyList<int> ChildrenOf(State state) => _childTokens[state.Node];

    /// <summary>排名用的偏置：<c>committed + S(node) − anchor</c>，后两项是「正在匹配、尚未完成」的暂存分。</summary>
    public double Bias(State state) => state.Committed + _score[state.Node] - state.Anchor;

    /// <summary>发射 token 后的状态转移。</summary>
    public State Step(State state, int token)
    {
        int node;
        double anchor = state.Anchor;
        if (_edges.TryGetValue(EdgeKey(state.Node, token), out var child))
        {
            node = child;
        }
        else
        {
            // 沿失配链找第一个有子节点 token 的祖先；找不到回到根。暂存分全部撤回。
            int candidate = state.Node;
            node = 0;
            while (candidate != 0)
            {
                candidate = _fail[candidate];
                if (_edges.TryGetValue(EdgeKey(candidate, token), out var target))
                {
                    node = target;
                    break;
                }
            }
            anchor = 0;
        }
        double committed = state.Committed;
        int hits = state.Hits;
        if (_isEnd[node])
        {
            var reached = _score[node];
            committed += reached - anchor;
            anchor = reached;
            hits++;
        }
        return new State(node, committed, anchor, hits);
    }
}
