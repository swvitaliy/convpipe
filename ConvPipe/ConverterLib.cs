using System.Collections;
using System.Text.RegularExpressions;
using Jint;
using NLua;
using org.matheval;

namespace ConvPipe;

internal static class StringTokenizer
{
    // https://stackoverflow.com/a/14655145/4731483
    public static string[] Tokenize(this String str)
    {
        //var re = new Regex("(\\S+|\"[^\"]*\")");
        var re = new Regex(@"[\""].+?[\""]|[^ ]+");
        var marr = re.Matches(str);

        return marr.Select(m => m.Value)
            //.Select(v => v.Trim('"'))
            .ToArray();
    }
}

public class ConverterLib
{
    public static ConverterLib CreateWithDefaults(string luaScript = null, string jsScript = null, Dictionary<string, object> globRef = null)
    {
        var convLib = new ConverterLib();
        DefaultConverters.InitializeLib(convLib);

        if (luaScript != null)
        {
            var lc = new LuaConverters(luaScript);
            lc.InitializeLib(convLib);
        }

        if (jsScript != null)
        {
            var jsc = new JsConverter(jsScript);
            jsc.InitializeLib(convLib);
        }

        if (globRef != null)
        {
            var pfc = new PathFinderConverters(globRef);
            pfc.InitializeLib(convLib);
        }

        return convLib;
    }

    public Dictionary<string, Func<object, string[], object>> Converters { get; } = new();
    public Dictionary<string, Func<object[], string[], object>> NAryConverters { get; } = new();

    public bool IsNAryConverter(string conv)
    {
        var i = conv.IndexOf(' ');
        if (i < 0)
            return false;
        var key = conv[..i];
        return NAryConverters.ContainsKey(key);
    }

    public object ConvertExpr(string[] expr, object val)
    {
        //string[] expr = convExpr.Split(' ').Select(str => str.Trim()).ToArray();
        if (expr.Length == 0)
            throw new Exception("expect converter name");
        var convName = expr[0];
        var convArgs = expr[1..];
        if (!Converters.ContainsKey(convName))
            throw new Exception($"converter \"{convName}\" not found");

        var conv = Converters[convName];
        return conv(val, convArgs);
    }

    public object ConvertPipe(string pipeExpr, object val)
    {
        //var pipe = pipeExpr.Split('|').Select(str => str.Trim());
        var pipe = PipeTokenize(pipeExpr);

        // foreach (var conv in pipe)
        //     val = ConvertExpr(conv, val);
        //
        // return val;

        object ans = val;
        foreach (var conv in pipe)
        {
            if (ans is IEnumerable<object>)
                ans = ConvertExprArray(conv, (object[])ans);
            else
                ans = ConvertExpr(conv, ans);
        }

        if (ans is IDestObject d)
            ans = d.Origin;

        return ans;
    }

    public object ConvertExprArray(string[] expr, object[] val)
    {
        //string[] expr = convExpr.Split(' ').Select(str => str.Trim()).ToArray();
        if (expr.Length == 0)
            throw new Exception("expect converter name");
        var convName = expr[0];
        var convArgs = expr[1..];
        if (!NAryConverters.ContainsKey(convName))
            throw new Exception($"converter \"{convName}\" not found");

        var conv = NAryConverters[convName];
        return conv(val, convArgs);
    }

    public object ConvertPipeArray(string pipeExpr, object[] val)
    {
        var pipe = PipeTokenize(pipeExpr);
        object ans = val;
        foreach (var conv in pipe)
        {
            if (ans is IEnumerable<object>)
                ans = ConvertExprArray(conv, (object[])ans);
            else
                ans = ConvertExpr(conv, ans);
        }

        if (ans is IDestObject d)
            ans = d.Origin;

        return ans;
    }

    private static string[][] PipeTokenize(string pipeExpr)
        => pipeExpr.Tokenize()
            .Aggregate(
                new List<List<string>> { new() },
                (acc, tok) =>
                {
                    if (tok == "|")
                        acc.Add(new List<string>());
                    else
                        acc.Last().Add(tok);

                    return acc;
                })
            .Select(item =>
                item.Where(s => !string.IsNullOrEmpty(s)).ToArray())
            .Where(arr => arr.Length > 0)
            .ToArray();
}

public class LuaConverters : IDisposable
{
    public LuaConverters(string libScript)
    {
        LibScript = libScript;
        LuaState = new Lua();
        LuaState.DoString(LibScript);
    }

    public void Dispose()
        => LuaState?.Dispose();


    public string LibScript { get; }
    private Lua LuaState { get; }

    public void InitializeLib(ConverterLib convLib)
    {
        convLib.Converters.Add("Lua", LuaRun);
        convLib.NAryConverters.Add("Lua", LuaRunN);
    }

    object LuaRun(object val, string[] args)
    {
        var fnName = args[0];
        if (LuaState[fnName] is not LuaFunction fn)
            throw new ArgumentException("Unknown lua func " + fnName);
        return fn.Call(val, args[1..]).First();
    }

    object LuaRunN(object[] vals, string[] args)
    {
        var fnName = args[0];
        if (LuaState[fnName] is not LuaFunction fn)
            throw new ArgumentException("Unknown lua func " + fnName);
        return fn.Call(vals, args[1..]).First();
    }
}

public class JsConverter : IDisposable
{
    public JsConverter(string js, string modulesDir = null)
    {
        LibScript = js;
        JsEngine = new Jint.Engine(options => {
                options.LimitMemory(1_000_000);
                options.TimeoutInterval(TimeSpan.FromSeconds(4));
                options.MaxStatements(1000);
                if (!string.IsNullOrEmpty(modulesDir))
                    options.EnableModules(modulesDir);
            })
        .Execute(LibScript);
    }

    public void Dispose()
        => JsEngine?.Dispose();

    public string LibScript { get; }
    public Jint.Engine JsEngine { get; }

    public void InitializeLib(ConverterLib convLib)
    {
        convLib.Converters.Add("Js", JsRun);
        convLib.NAryConverters.Add("Js", JsRunN);
    }

    object JsRun(object val, string[] args)
    {
        var fnName = args[0];
        return JsEngine.Invoke(fnName, val).ToObject();
    }

    object JsRunN(object[] vals, string[] args)
    {
        var fnName = args[0];
        return JsEngine.Invoke(fnName, (object) vals).ToObject();
    }
}

public class PathFinderConverters
{
    public void InitializeLib(ConverterLib convLib)
    {
        convLib.Converters.Add("ByPath", ByPath);
        convLib.NAryConverters.Add("ByPath", ByPathN);
    }

    public PathFinderConverters(Dictionary<string, object> globRef)
    {
        _pFinder = new PathFinder(globRef.Keys.ToArray(), globRef);
    }

    private readonly PathFinder _pFinder;

    object FindPath(object val, string path, bool multi)
        => _pFinder.GetValue(val, path, multi, out _, out _, out _, out _);

    object ByPath(object val, string[] args)
    {
        var path = args[0].Trim('"');
        return FindPath(val, path, multi: true);
    }

    object ByPathN(object[] vals, string[] args)
    {
        var path = args[0].Trim('"');
        return FindPath(vals, path, multi: true);
    }

}

public static class DefaultConverters
{
    public static void InitializeLib(ConverterLib convLib)
    {
        convLib.Converters.Add("Convert", ConvertFn);
        convLib.Converters.Add("ToString", ToString);
        convLib.Converters.Add("ToLower", ToLower);
        convLib.Converters.Add("ToUpper", ToUpper);
        convLib.Converters.Add("AsFirstItemOfArray", AsFirstItemOfArray);
        convLib.Converters.Add("AsArrayWithOneItem", AsArrayWithOneItem);
        convLib.Converters.Add("Split", Split);
        convLib.Converters.Add("ConstValue", ConstValue);
        convLib.Converters.Add("Property", Property);
        convLib.Converters.Add("ItemProperty", ItemProperty);
        convLib.Converters.Add("ExprEval", ExprEval);
        convLib.NAryConverters.Add("OneOf", OneOf);
        convLib.NAryConverters.Add("Join", Join);
        convLib.NAryConverters.Add("IfThenElse", IfThenElse);
        convLib.NAryConverters.Add("ExprEvalN", ExprEvalN);
        convLib.NAryConverters.Add("First", FirstFn);
        convLib.NAryConverters.Add("Last", LastFn);
    }

    static object FirstFn(object[] vals, string[] args)
    {
        return vals?.FirstOrDefault();
    }

    static object LastFn(object[] vals, string[] args)
    {
        return vals?.LastOrDefault();
    }
    static object ConvertFn(object val, string[] args)
    {
        if (val == null)
            return null;

        var methodName = args[0];
        var method = typeof(Convert).GetMethod(methodName, new Type[] { val.GetType() });

        if (method == null)
            throw new ArgumentException("Unknown method " + methodName);

        return method.Invoke(null, new [] {val});
    }

    static object ToString(object val, string[] args)
    {
        return val?.ToString();
    }

    static object ToLower(object val, string[] args)
    {
        if (val == null)
            return null;

        return ((string)val).ToLower();
    }

    static object ToUpper(object val, string[] args)
    {
        if (val == null)
            return null;

        return ((string)val).ToUpper();
    }

    static object AsArrayWithOneItem(object val, string[] args)
    {
        if (val == null)
            return null;

        var t = val.GetType();
        var a = Array.CreateInstance(t, 1);
        a.SetValue(val, 0);

        return a;
    }

    static object AsFirstItemOfArray(object val, string[] args)
    {
        return new string[] { (string)val };
    }

    static object Split(object val, string[] args)
    {
        if (val == null)
            return null;

        if (args.Length < 1)
            throw new Exception("expected delimiter");

        var delim = args[0];
        delim = Regex.Unescape(delim.Trim().Trim('"').Trim('"'));
        // Console.WriteLine("Delim is [" + delim + "]");

        return ((string)val).Split(delim);
    }

    static object ConstValue(object val, string[] args)
    {
        if (args.Length < 1)
            throw new Exception("expected value");

        return args[0];
    }

    static object Property(object val, string[] args)
    {
        if (args.Length < 1)
            throw new Exception("expected property");

        var propName = args[0];
        var dest = DestObject.Create(val);

        return dest.GetProperty(propName);
    }

    static object ItemProperty(object val, string[] args)
    {
        if (args.Length < 1)
            throw new Exception("expected property");

        if (val is not IEnumerable)
            throw new Exception("expected enumerable property");

        var propName = args[0];
        var dest = DestObject.Create(val);

        if (dest is DynamicDestObject)
        {
            var ret = dest.GetProperty(propName);
            // ret = (KeyValuePair<string, object>)ret;
            return ret;
        }

        return val;
    }

    static object OneOf(object[] vals, string[] args)
    {
        var order = vals.Select((_, i) => i)
            .ToArray();
        if (args.Length != 0 && args.Length != vals.Length)
            throw new Exception("expect 0 or "+vals.Length+" arguments");

        if (args.Length > 0)
        {
            for (uint i = 0; i < args.Length; ++i)
                order[i] = int.Parse(args[i]);
        }

        for (uint i = 0; i < order.Length; ++i)
        {
            if (vals[order[i]] != null)
                return vals[order[i]];
        }

        return null;
    }

    static object IfThenElse(object[] vals, string[] args)
    {
        var order = vals.Select((_, i) => i)
            .ToArray();
        if (args.Length != 0 && args.Length != vals.Length)
            throw new Exception("expect 0 or "+vals.Length+" arguments");

        if (args.Length > 0)
        {
            for (uint i = 0; i < args.Length; ++i)
                order[i] = int.Parse(args[i]);
        }

        for (uint i = 0; i < order.Length; ++i)
        {
            if (vals[order[i]] != null)
                return vals[order[i]];
        }

        return null;
    }

    static object Join(object[] vals, string[] args)
    {
        if (args.Length != 0 && args.Length != 1 && args.Length != vals.Length + 1)
            throw new Exception("expect 1 or "+(vals.Length+1)+" arguments");

        var delim = args.Length == 0 ? string.Empty : args[0].Trim('"');
        delim = System.Text.RegularExpressions.Regex.Unescape(delim);
        args = args.Length > 1 ? args[1..] : Array.Empty<string>();

        var order = vals.Select((_, i) => i)
            .ToArray();
        if (args.Length > 0)
            for (uint i = 0; i < args.Length; ++i)
                order[i] = int.Parse(args[i]);

        List<string> ans = new();
        for (uint i = 0; i < order.Length; ++i)
        {
            var val = vals[order[i]];
            if (val != null)
                ans.Add(val.ToString());
        }

        return string.Join(delim, ans);
    }

    static object ExprEval(object val, string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("expression expected");
        Expression expr = new(args[0].Trim('"'));
        if (args.Length > 1)
            expr.Bind(args[1], val ?? 0);
        return expr.Eval();
    }

    static object ExprEvalN(object[] vals, string[] args)
    {
        Expression expr = new(args[0].Trim('"'));
        for (int i = 1; i < args.Length; ++i)
        {
            if ((i - 1) >= vals.Length)
                throw new ArgumentException("expected value for " + args[i]);
            expr.Bind(args[i], vals[i - 1]);
        }

        return expr.Eval();
    }
}