using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Ganss.Xss;

/// <summary>
/// An immutable snapshot of everything a single sanitize call reads from the mutable
/// <see cref="HtmlSanitizer"/> configuration: the allowed tag, attribute, URI scheme and
/// CSS sets, the boolean options, the CSS value rule, the formatters and factories, and
/// the event handler lists.
/// </summary>
/// <remarks>
/// A snapshot is captured when a public sanitize method is entered, and the traversal
/// reads only the snapshot afterwards, so one call always observes a single consistent
/// version of the configuration even when another thread mutates the sanitizer mid-call;
/// a change takes effect from the next call. The sets are copied, while delegates,
/// formatters and the regex are immutable for the sanitizer's purposes and are captured
/// by reference.
/// <para>
/// The snapshot is created per call and is never cached or stored on the sanitizer, so it
/// cannot keep a replaced event handler - or any other user delegate - alive past the end
/// of the call that captured it, and nothing accumulates over repeated calls.
/// </para>
/// </remarks>
internal sealed class SanitizerPolicy
{
    public ISet<string> AllowedTags { get; set; } = new HashSet<string>();
    public ISet<string> AllowedSchemes { get; set; } = new HashSet<string>();
    public ISet<string> AllowedAttributes { get; set; } = new HashSet<string>();
    public ISet<string> UriAttributes { get; set; } = new HashSet<string>();
    public ISet<string> UriListAttributes { get; set; } = new HashSet<string>();
    public ISet<string> AllowedCssProperties { get; set; } = new HashSet<string>();
    public ISet<string> AllowedClasses { get; set; } = new HashSet<string>();
    public ISet<CssRuleType> AllowedAtRules { get; set; } = new HashSet<CssRuleType>();

    public bool AllowDataAttributes { get; set; }
    public bool AllowCssCustomProperties { get; set; }
    public bool KeepChildNodes { get; set; }

    public Regex DisallowCssPropertyValue { get; set; } = HtmlSanitizer.DefaultDisallowedCssPropertyValue;

    public Action<IComment> EncodeComment { get; set; } = _ => { };
    public Action<IElement> EncodeLiteralTextElementContent { get; set; } = _ => { };
    public Func<HtmlParser> HtmlParserFactory { get; set; } = () => throw new InvalidOperationException();
    public IMarkupFormatter OutputFormatter { get; set; } = HtmlFormatter.Instance;
    public IStyleFormatter StyleFormatter { get; set; } = CssStyleFormatter.Instance;

    public EventHandler<PostProcessDomEventArgs>? PostProcessDom { get; set; }
    public EventHandler<PostProcessNodeEventArgs>? PostProcessNode { get; set; }
    public EventHandler<RemovingTagEventArgs>? RemovingTag { get; set; }
    public EventHandler<RemovingAttributeEventArgs>? RemovingAttribute { get; set; }
    public EventHandler<RemovingStyleEventArgs>? RemovingStyle { get; set; }
    public EventHandler<RemovingAtRuleEventArgs>? RemovingAtRule { get; set; }
    public EventHandler<RemovingCommentEventArgs>? RemovingComment { get; set; }
    public EventHandler<RemovingCssClassEventArgs>? RemovingCssClass { get; set; }
    public EventHandler<FilterUrlEventArgs>? FilterUrl { get; set; }
    public EventHandler<FilterCssRuleEventArgs>? FilterCssRule { get; set; }

    /// <summary>
    /// Copies a set, keeping the comparer the original was created with so the snapshot
    /// answers lookups exactly the way the live set would have.
    /// </summary>
    /// <remarks>
    /// A caller mutating the set on another thread while the copy enumerates it makes the
    /// enumerator throw. Retrying rather than failing the call keeps a concurrent change from
    /// breaking a sanitize that started before it: the retried copy simply reflects a later
    /// state, which the caller could equally have observed by starting its change earlier.
    /// </remarks>
    public static HashSet<string> Copy(ISet<string> set)
    {
        if (set is not HashSet<string> hashSet)
            return new HashSet<string>(set);

        while (true)
        {
            try
            {
                return new HashSet<string>(hashSet, hashSet.Comparer);
            }
            catch (InvalidOperationException)
            {
                // The set changed under the enumeration; copy again.
            }
        }
    }

    /// <summary>
    /// Copies an at-rule set, keeping the comparer as <see cref="Copy(ISet{string})"/> does.
    /// </summary>
    public static HashSet<CssRuleType> Copy(ISet<CssRuleType> set)
    {
        if (set is not HashSet<CssRuleType> hashSet)
            return new HashSet<CssRuleType>(set);

        while (true)
        {
            try
            {
                return new HashSet<CssRuleType>(hashSet, hashSet.Comparer);
            }
            catch (InvalidOperationException)
            {
                // The set changed under the enumeration; copy again.
            }
        }
    }
}
