﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Xrm.Oss.XTL.Templating;

namespace Xrm.Oss.XTL.Interpreter
{
    public class XTLInterpreter
    {
        private StringReader _reader = null;
        private string _input;
        private char _previous;
        private char _current;
        private bool _eof;

        private Entity _primary;
        private IOrganizationService _service;
        private ITracingService _tracing;
        private OrganizationConfig _organizationConfig;

        public delegate ValueExpression FunctionHandler(Entity primary, IOrganizationService service, ITracingService tracing, OrganizationConfig organizationConfig, List<ValueExpression> parameters);

        private Dictionary<string, FunctionHandler> _handlers = new Dictionary<string, FunctionHandler>
        {
            { "If", FunctionHandlers.If },
            { "Or", FunctionHandlers.Or },
            { "And", FunctionHandlers.And },
            { "Not", FunctionHandlers.Not },
            { "IsNull", FunctionHandlers.IsNull },
            { "IsEqual", FunctionHandlers.IsEqual },
            { "Value", FunctionHandlers.GetValue },
            { "RecordUrl", FunctionHandlers.GetRecordUrl },
            { "Fetch", FunctionHandlers.Fetch },
            { "RecordTable", FunctionHandlers.RenderRecordTable },
            { "PrimaryRecord", FunctionHandlers.GetPrimaryRecord },
            { "First", FunctionHandlers.First },
            { "Last", FunctionHandlers.Last},
            { "Concat", FunctionHandlers.Concat },
            { "Substring", FunctionHandlers.Substring },
            { "Replace", FunctionHandlers.Replace },
            { "Array", FunctionHandlers.Array },
            { "Join", FunctionHandlers.Join },
            { "NewLine", FunctionHandlers.NewLine },
            { "DateTimeNow", FunctionHandlers.DateTimeNow },
            { "DateTimeUtcNow", FunctionHandlers.DateTimeUtcNow },
            { "DateToString", FunctionHandlers.DateToString },
            { "Static", FunctionHandlers.Static },
            { "IsLess", FunctionHandlers.IsLess },
            { "IsLessEqual", FunctionHandlers.IsLessEqual },
            { "IsGreater", FunctionHandlers.IsGreater },
            { "IsGreaterEqual", FunctionHandlers.IsGreaterEqual }
        };

        public XTLInterpreter(string input, Entity primary, OrganizationConfig organizationConfig, IOrganizationService service, ITracingService tracing)
        {
            _primary = primary;
            _service = service;
            _tracing = tracing;
            _organizationConfig = organizationConfig;
            _input = input;

            _reader = new StringReader(input ?? string.Empty);
            GetChar();
            SkipWhiteSpace();
        }

        /// <summary>
        /// Reads the next character and sets it as current. Old current char becomes previous.
        /// </summary>
        /// <returns>True if read succeeded, false if end of input</returns>
        private void GetChar()
        {
            _previous = _current;
            var character = _reader.Read();
            _current = (char)character;
            
            if (character == -1)
            {
                _eof = true;
            }
        }

        private void Expected(string expected)
        {
            throw new InvalidPluginExecutionException($"{expected} expected after '{_previous}{_current}'");
        }

        private void SkipWhiteSpace() 
        {
            while(char.IsWhiteSpace(_current) && !_eof) {
                GetChar();
            }
        }

        private void Match (char c) 
        {
            if (_current != c) 
            {
                Expected(c.ToString(CultureInfo.InvariantCulture));
            }

            GetChar();
            SkipWhiteSpace();
        }

        private string GetName() 
        {
            SkipWhiteSpace();

            if (!char.IsLetter(_current)) {
                Expected($"An identifier was expected, but the current char {_current} is not a letter. ");
            }

            var name = string.Empty;

            while (char.IsLetterOrDigit(_current) && !_eof) {
                name += _current;
                GetChar();
            }

            SkipWhiteSpace();
            return name;
        }

        private List<ValueExpression> Expression()
        {
            var returnValue = new List<ValueExpression>();

            do
            {
                if (_current == ',') {
                    GetChar();
                }
                else if (_current == '"')
                {
                    var stringConstant = string.Empty;
                    
                    // Skip opening quote
                    GetChar();

                    // Allow to escape quotes by backslashes
                    while ((_current != '"' || _previous == '\\') && !_eof)
                    {
                        stringConstant += _current;
                        GetChar();
                    }

                    // Skip closing quote
                    GetChar();
                    returnValue.Add(new ValueExpression(stringConstant, stringConstant));
                }
                else if (char.IsDigit(_current))
                {
                    var digit = 0;
                    var fractionalPart = 0;
                    var processingFractionalPart = false;

                    do
                    {
                        if (_current != '.')
                        {
                            if (processingFractionalPart)
                            {
                                fractionalPart = fractionalPart * 10 + int.Parse(_current.ToString(CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                digit = digit * 10 + int.Parse(_current.ToString(CultureInfo.InvariantCulture));
                            }
                        }
                        else
                        {
                            processingFractionalPart = true;
                        }

                        GetChar();
                    } while ((char.IsDigit(_current) || _current == '.') && !_eof);

                    switch(_current)
                    {
                        case 'd':
                            double doubleValue = digit + fractionalPart / Math.Pow(10, (fractionalPart.ToString(CultureInfo.InvariantCulture).Length));
                            returnValue.Add(new ValueExpression(doubleValue.ToString(CultureInfo.InvariantCulture), doubleValue));
                            GetChar();
                            break;
                        case 'm':
                            decimal decimalValue = digit + fractionalPart / (decimal) Math.Pow(10, (fractionalPart.ToString(CultureInfo.InvariantCulture).Length));
                            returnValue.Add(new ValueExpression(decimalValue.ToString(CultureInfo.InvariantCulture), decimalValue));
                            GetChar();
                            break;
                        default:
                            if (processingFractionalPart)
                            {
                                throw new InvalidDataException("For defining numbers with fractional parts, please append d (for double) or m (for decimal) to your number");
                            }

                            returnValue.Add(new ValueExpression(digit.ToString(CultureInfo.InvariantCulture), digit));
                            break;
                    }

                }
                else if (_current == ')')
                {
                    // Parameterless function encountered
                }
                else if (_current == ']') 
                {
                    // Empty array
                }
                // The first char of a function must not be a digit
                else
                {
                    returnValue.Add(Formula());
                }

                SkipWhiteSpace();
            } while (_current != ')' && _current != ']');

            return returnValue;
        }

        private ValueExpression ApplyExpression (string name, List<ValueExpression> parameters) 
        {
            if (!_handlers.ContainsKey(name)) {
                throw new InvalidPluginExecutionException($"Function {name} is not known!");
            }

            var lazyExecution = new Lazy<ValueExpression>(() =>
            {
                _tracing.Trace($"Processing handler {name}");
                var result = _handlers[name](_primary, _service, _tracing, _organizationConfig, parameters);
                _tracing.Trace($"Successfully processed handler {name}");

                return result;
            });

            return new ValueExpression(lazyExecution);
        }

        private ValueExpression Formula()
        {
            SkipWhiteSpace();

            if (_current == '[') {
                Match('[');
                var arrayParameters = Expression();
                Match(']');

                return ApplyExpression("Array", arrayParameters);
            }
            else if (_current == '{')
            {
                Match('{');
                var dictionary = new Dictionary<string, object>();
                var firstRunPassed = false;

                do
                {
                    SkipWhiteSpace();

                    if (firstRunPassed)
                    {
                        Match(',');
                        SkipWhiteSpace();
                    }
                    else
                    {
                        firstRunPassed = true;
                    }

                    Match('"');

                    var name = GetName();

                    Match('"');
                    SkipWhiteSpace();
                    Match(':');
                    SkipWhiteSpace();

                    dictionary[name] = Formula().Value;
                    
                    SkipWhiteSpace();
                } while (_current != '}');

                Match('}');

                return new ValueExpression(string.Join(", ", dictionary.Select(p => $"{p.Key}: {p.Value}")), dictionary);
            }
            else {
                var name = GetName();

                switch(name)
                {
                    case "true":
                        return new ValueExpression(bool.TrueString, true);
                    case "false":
                        return new ValueExpression(bool.FalseString, false);
                    case "null":
                        return new ValueExpression( null );
                    default:
                        Match('(');
                        var parameters = Expression();
                        Match(')');

                        return ApplyExpression(name, parameters);
                }
            }
        }

        public string Produce() 
        {
            _tracing.Trace($"Initiating interpreter");

            if (string.IsNullOrWhiteSpace(_input)) {
                _tracing.Trace("No formula passed, exiting");
                return string.Empty;
            }

            var output = Formula();

            return output?.Text;
        }
    }
}
