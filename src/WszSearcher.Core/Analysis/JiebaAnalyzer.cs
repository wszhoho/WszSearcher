using System.Diagnostics;
using JiebaNet.Segmenter;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Tokenattributes;

namespace WszSearcher.Core.Analysis;

/// <summary>
/// 基于 jieba.NET 的中文分词器——替换 StandardAnalyzer
/// jieba 采用前缀词典 + HMM 模型，支持高质量中文分词
/// JiebaSegmenter 为全局单例（约 200MB 词典内存，避免重复加载）
/// </summary>
public class JiebaAnalyzer : Analyzer
{
    // 全局单例 JiebaSegmenter（词典约 200MB，只加载一次）
    private static readonly Lazy<JiebaSegmenter> _segmenter = new(() =>
    {
        var seg = new JiebaSegmenter();
        seg.Cut("预热加载中文词典"); // 触发词典加载
        Debug.WriteLine("jieba 中文词典加载完成（全局单例）");
        return seg;
    });

    private JiebaSegmenter Segmenter => _segmenter.Value;

    public JiebaAnalyzer()
    {
        // 构造函数中访问 _segmenter.Value 触发单例初始化
        _ = _segmenter.Value;
    }

    public override TokenStream TokenStream(string fieldName, System.IO.TextReader reader)
    {
        return new JiebaTokenStream(Segmenter, reader);
    }

    /// <summary>
    /// 将 jieba 分词结果适配为 Lucene TokenStream
    /// 使用 Lucene 3.0.3 的 Attribute API
    /// </summary>
    private class JiebaTokenStream : TokenStream
    {
        private readonly JiebaSegmenter _segmenter;
        private readonly string _text;
        private IEnumerator<string>? _tokens;

        // Lucene 3.0.3 通过接口获取 Attribute，转为具体类以调用 Setter
        private readonly TermAttribute _termAtt;
        private readonly OffsetAttribute _offsetAtt;
        private readonly PositionIncrementAttribute _posIncrAtt;

        private int _currentOffset;

        public JiebaTokenStream(JiebaSegmenter segmenter, System.IO.TextReader reader)
        {
            _segmenter = segmenter;
            _text = reader.ReadToEnd();

            // Lucene.NET 3.0.3: AddAttribute<T> 要求 T 是继承了 Attribute 的接口
            _termAtt = (TermAttribute)AddAttribute<ITermAttribute>();
            _offsetAtt = (OffsetAttribute)AddAttribute<IOffsetAttribute>();
            _posIncrAtt = (PositionIncrementAttribute)AddAttribute<IPositionIncrementAttribute>();
        }

        public override bool IncrementToken()
        {
            if (_tokens is null)
            {
                _tokens = _segmenter.CutForSearch(_text).GetEnumerator();
                _currentOffset = 0;
            }

            // 循环跳过空白词，避免无限递归导致 StackOverflow
            while (_tokens.MoveNext())
            {
                var word = _tokens.Current;
                if (string.IsNullOrWhiteSpace(word))
                    continue;

                // 估算词在原文中的位置（防止 currentOffset 越界）
                int start, end;
                if (_currentOffset < _text.Length)
                {
                    var idx = _text.IndexOf(word, _currentOffset, StringComparison.Ordinal);
                    start = idx >= 0 ? idx : _currentOffset;
                }
                else
                {
                    start = _text.Length;
                }
                end = Math.Min(start + word.Length, _text.Length);
                _currentOffset = end;

                // 设置 Token 属性（Lucene 3.0.3 通过 Attribute 对象）
                _termAtt.SetTermBuffer(word);
                _offsetAtt.SetOffset(start, end);
                _posIncrAtt.PositionIncrement = 1;

                return true;
            }

            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tokens?.Dispose();
            }
            // 注：Lucene.NET 3.0.3 的 TokenStream.Dispose(bool) 是抽象的，无需调用 base
        }
    }
}
