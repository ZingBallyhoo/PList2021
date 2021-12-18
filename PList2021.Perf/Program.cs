using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using PListNet;
using PListNet.Nodes;

namespace PList2021.Perf
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Hello World!");
            var summary = BenchmarkRunner.Run<Perf>();
        }
    }
    
    [MemoryDiagnoser]
    public class Perf
    {
        private static byte[] s_data;
        
        [GlobalSetup]
        public void Setup()
        {
            s_data = File.ReadAllBytes("alargeplistofyourchoosing.plist");
        }
        
        [Benchmark]
        public object NoPool()
        {
            BinaryFormatParser.s_poolStrings = false;
            return BinaryFormatParser.Parse(s_data);
        }
        
        [Benchmark]
        public object Pool()
        {
            BinaryFormatParser.s_poolStrings = true;
            return BinaryFormatParser.Parse(s_data);
        }
        
        [Benchmark]
        public object Old()
        {
            using var stream = new MemoryStream(s_data);
            var node = PList.Load(stream);
            return PListToJson(node);
        }

        private static object PListToJson(PNode node)
        {
            //Console.Out.WriteLine(node.ToString());
            if (node is DictionaryNode dict)
            {
                var newDict = new Dictionary<string, object>();
                foreach (var pair in dict)
                {
                    newDict[pair.Key] = PListToJson(pair.Value);
                }
                return newDict;
            } else if (node is IntegerNode integer)
            {
                return integer.Value;
            } else if (node is StringNode stringNode)
            {
                return stringNode.Value;
            } else if (node is BooleanNode booleanNode)
            {
                return booleanNode.Value;
            } else if (node is ArrayNode arrayNode)
            {
                var newArray = new object[arrayNode.Count];
                for (var i = 0; i < arrayNode.Count; i++)
                {
                    newArray[i] = PListToJson(arrayNode[i]);
                }

                return newArray;
            } else if (node is RealNode realNode)
            {
                return realNode.Value;
            }
            throw new NotImplementedException(node.GetType().Name);
        }

    }
}