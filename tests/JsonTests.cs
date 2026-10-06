using System;
using System.Collections.Generic;
using System.IO;

namespace Capsule
{
    public static class JsonTests
    {
        public static void Run()
        {
            ParsesTypesAndKeepsKeyOrder();
            GetWalksNestedObjects();
            WritesIndentedJson();
            WritesEscapesAndRoundTrips();
            TryParseReturnsNullForBadInput();
            AtomicWriteCreatesAndReplaces();
            ReadTailStartsAtALine();
            ClockConvertsTimes();
            ParseErrorsNeverCarryTheInput();
        }

        static void ParsesTypesAndKeepsKeyOrder()
        {
            var o = Json.Obj(Json.Parse("{\"b\":1,\"a\":[2.5,12345678901,true,null],\"c\":\"x\"}"));
            TestRunner.Eq("b,a,c", string.Join(",", o.Keys), "key order");
            object[] a = Json.Arr(o["a"]);
            TestRunner.Near(2.5, Json.Num(a[0]).Value, "decimal");
            TestRunner.Near(12345678901, Json.Num(a[1]).Value, "long");
            TestRunner.Eq(true, a[2], "bool");
            TestRunner.Check(a[3] == null, "null");
            TestRunner.Eq(null, Json.Num("7"), "a string is not a number");
        }

        static void GetWalksNestedObjects()
        {
            object root = Json.Parse("{\"x\":{\"y\":{\"z\":5}}}");
            TestRunner.Near(5, Json.Num(Json.Get(root, "x", "y", "z")).Value, "nested get");
            TestRunner.Check(Json.Get(root, "x", "missing", "z") == null, "missing step gives null");
            TestRunner.Check(Json.Get(root, "x", "y", "z", "deeper") == null, "stepping into a number gives null");
            TestRunner.Check(Json.Get(null, "x") == null, "null root");
        }

        static void WritesIndentedJson()
        {
            var d = new Dictionary<string, object>();
            d["name"] = "notch";
            d["list"] = new object[] { 1, "two" };
            d["empty"] = new object[0];
            d["obj"] = new Dictionary<string, object>();
            d["args"] = new string[] { "--probe" };
            d["flag"] = false;
            d["half"] = 0.5;
            string expected = "{\n  \"name\": \"notch\",\n  \"list\": [\n    1,\n    \"two\"\n  ],\n  \"empty\": [],\n  \"obj\": {},\n  \"args\": [\n    \"--probe\"\n  ],\n  \"flag\": false,\n  \"half\": 0.5\n}";
            TestRunner.Eq(expected, Json.Write(d), "indented output");
        }

        static void WritesEscapesAndRoundTrips()
        {
            var d = new Dictionary<string, object>();
            d["path"] = "C:\\Users\\A \"B\"\n\u0001é";
            d["big"] = 1790683200000L;
            string text = Json.Write(d);
            var back = Json.Obj(Json.Parse(text));
            TestRunner.Eq(d["path"], back["path"], "string round trip");
            TestRunner.Near(1790683200000, Json.Num(back["big"]).Value, "long round trip");
            TestRunner.Check(text.Contains("\\u0001"), "control characters escaped");
        }

        static void TryParseReturnsNullForBadInput()
        {
            TestRunner.Check(Json.TryParse("{not json") == null, "malformed");
            TestRunner.Check(Json.TryParse("") == null, "empty");
            TestRunner.Check(Json.TryParse(null) == null, "null");
        }

        static void AtomicWriteCreatesAndReplaces()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "f.json");
            TestRunner.Check(Files.ReadText(path) == null, "missing file reads as null");
            TestRunner.Check(Files.WriteAtomic(path, "one"), "create");
            TestRunner.Check(Files.WriteAtomic(path, "two"), "replace");
            TestRunner.Eq("two", Files.ReadText(path), "content");
            TestRunner.Eq(1, Directory.GetFiles(Path.GetDirectoryName(path)).Length, "no temp files left behind");
        }

        static void ReadTailStartsAtALine()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "log.jsonl");
            File.WriteAllText(path, "first line that is long\nsecond\nthird\n");
            TestRunner.Eq("second\nthird\n", Files.ReadTail(path, 14), "partial first line dropped");
            TestRunner.Eq("third\n", Files.ReadTail(path, 6), "tail starting exactly on a line");
            TestRunner.Eq("first line that is long\nsecond\nthird\n", Files.ReadTail(path, 1000), "whole file when small");
        }

        static void ClockConvertsTimes()
        {
            DateTime t = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
            TestRunner.Eq(1790683200000L, Clock.ToMs(t), "to ms");
            TestRunner.Eq(t, Clock.FromMs(1790683200000L), "from ms");
            TestRunner.Eq(1790694000000L, Clock.ParseIsoMs("2026-09-29T15:00:00.000000+00:00"), "iso with offset");
            TestRunner.Eq(1790582460000L, Clock.ParseIsoMs("2026-09-28T08:01:00.000Z"), "iso with Z");
            TestRunner.Eq(0L, Clock.ParseIsoMs("soon"), "junk");
        }

        static void ParseErrorsNeverCarryTheInput()
        {
            string secret = "{\"claudeAiOauth\":{\"accessToken\":\"sk-SECRET-TEST\"";
            try
            {
                Json.Parse(secret);
                TestRunner.Check(false, "malformed JSON throws");
            }
            catch (FormatException e)
            {
                TestRunner.Check(!e.Message.Contains("sk-SECRET-TEST") && e.InnerException == null, "the error carries none of the input");
                Log.Error("parse", e);
                TestRunner.Check(!File.ReadAllText(Paths.LogFile).Contains("sk-SECRET-TEST"), "so logging it cannot leak a token");
            }
        }
    }
}
