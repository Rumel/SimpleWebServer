using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CSharp;

namespace WebServer
{
    public class CWebTemplateProcessor : IScriptProcessor
    {
        private ICodeCompiler _compiler;

        private CompilerParameters _parameters;

        private const string _classTemplate = "using System;" +
            "using System.Collections.Generic;" +
            "namespace Server {" +
                "public class Executor {" +
                    "public void Execute(List<String> ExecutorList) {" +
                        "{0}" +
                    "}" +
                "}" +
            "}";

        public CWebTemplateProcessor()
        {
            var provider = new CSharpCodeProvider();

            _compiler = provider.CreateCompiler();

            _parameters = new CompilerParameters();

            _parameters.ReferencedAssemblies.Add("system.dll");

            _parameters.GenerateInMemory = true;

            _parameters.CompilerOptions = "/t:library";
        }

        public ScriptResult ProcessScript(string path, IDictionary<string, string> requestParameters)
        {
            var scriptBody = new StringBuilder();

            /* read the contents of the file into a string 
             * builder line by line to create a single 
             * string that represents the script */
            using (FileStream fs = File.OpenRead(path))
            {
                var reader = new StreamReader(fs);
                string line = null;
                while ((line = reader.ReadLine()) != null)
                {
                    scriptBody.Append(line);
                }
            }

            var body = scriptBody.ToString();

            var i = 0;
            var code = new List<CodeSegment>();
            while (i < body.Length)
            {
                if (body[i] == '@' && body[i + 1] == '{')
                {
                    var sb = new StringBuilder();
                    i = i + 2;
                    var foundEnd = false;
                    var brackets = 1;
                    while (!foundEnd)
                    {
                        if(body[i] == '}')
                        {
                            brackets--;
                            if (brackets == 0)
                            {
                                code.Add(new CodeSegment(sb.ToString(), CodeType.Evaluate));
                                foundEnd = true;
                            }
                            else
                            {
                                sb.Append(body[i]);
                            }
                        }
                        else if (body[i] == '{')
                        {
                            sb.Append('}');
                            brackets++;
                        }
                        else
                        {
                            sb.Append(body[i]);
                        }
                        i++;
                    }
                }
                else if (body[i] == '{')
                {
                    var sb = new StringBuilder();
                    i = i + 1;
                    var foundEnd = false;
                    var brackets = 1;
                    while (!foundEnd)
                    {
                        if (body[i] == '}')
                        {
                            brackets--;
                            if (brackets == 0)
                            {
                                code.Add(new CodeSegment(sb.ToString(), CodeType.Compile));
                                foundEnd = true;
                            }
                            else
                            {
                                sb.Append(body[i]);
                            }
                        }
                        else if (body[i] == '{')
                        {
                            sb.Append('}');
                            brackets++;
                        }
                        else
                        {
                            sb.Append(body[i]);
                        }
                        i++;
                    }
                }
                else
                {
                    i++;
                }
            }

            var html = scriptBody.ToString();
            var codeToCompile = new StringBuilder();
            int count = 0;
            foreach (var c in code)
            {
                if (c.CodeType == CodeType.Compile)
                {
                    html = html.Replace(c.WithBrackets(), "");
                }
                else
                {
                    html = html.Replace(c.WithBrackets(), String.Format("{{{0}}}", count++));
                }

                // Replace requests while we are at it
                var matches = Regex.Matches(c.Code, @"request\[(""|')([\w\d]+)(""|')\]");
                foreach (Match m in matches)
                {

                    if (requestParameters.ContainsKey(m.Groups[2].ToString()))
                    {
                        c.Code = c.Code.Replace(m.Groups[0].ToString(), '"' + Uri.UnescapeDataString(requestParameters[m.Groups[2].ToString()] + '"'));
                    }
                    else
                    {
                        c.Code = c.Code.Replace(m.Groups[0].ToString(), "\"\"");
                    }
                }

                if (c.CodeType == CodeType.Compile)
                {
                    codeToCompile.Append(c.Code);
                }
                else
                {
                    codeToCompile.Append("ExecutorList.Add(" + c.Code + ");");
                }
            }

            string source = _classTemplate.Replace("{0}", codeToCompile.ToString());

            CompilerResults result = _compiler.CompileAssemblyFromSource(_parameters, source);

            if (result.Errors.Count > 0)
            {
                StringBuilder errorBody = new StringBuilder();
                errorBody.Append("<html><body>");
                errorBody.Append("<h1>Script Compilation Errors</h1>");
                errorBody.Append("<p>The following errors occurred processing the requested resource</p>");
                errorBody.Append("<ul>");
                foreach (CompilerError error in result.Errors)
                {
                    errorBody.Append(string.Format("<li>{0}:{1} - Error: {2}</li>", error.Line, error.Column, error.ErrorText));
                }
                errorBody.Append("</ul>");
                errorBody.Append("</body></html>");

                /* the script result with the list of errors as the result */
                return new ScriptResult()
                {
                    Error = true,
                    Result = errorBody.ToString()
                };
            }

            System.Reflection.Assembly codeAssembly = result.CompiledAssembly;
            object instance = codeAssembly.CreateInstance("Server.Executor");

            Type instanceType = instance.GetType();
            MethodInfo executionMethod = instanceType.GetMethod("Execute", new Type[] { typeof(List<string>) });

            try
            {
                var resultList = new List<string>();

                executionMethod.Invoke(instance, new object[] { resultList });

                return new ScriptResult()
                {
                    Error = false,
                    Result = String.Format(html, resultList.ToArray())
                };
            }
            catch(Exception e)
            {
                return new ScriptResult()
                {
                    Error = true,
                    Result = string.Format("<html><body><h1>Runtime Error</h1><p>The following runtime error occurred: {0}</p>",
                        e.InnerException.Message)
                };
            }
        }
    }

    public class CodeSegment
    {
        public String Code;
        public CodeType CodeType;

        public CodeSegment(string code, CodeType codeType)
        {
            Code = code;
            CodeType = codeType;
        }

        public String WithBrackets()
        {
            if (CodeType == CodeType.Evaluate)
            {
                return String.Format("@{{{0}}}", Code);
            }

            return String.Format("{{{0}}}", Code);
        }

        public override string ToString()
        {
            return Code;
        }
    }

    public enum CodeType
    {
        Compile,
        Evaluate
    }
}
