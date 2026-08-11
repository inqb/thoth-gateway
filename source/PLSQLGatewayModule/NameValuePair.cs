using System;
using System.Collections.Generic;
using System.Web;

namespace PLSQLGatewayModule
{
    public enum ValueType
    {
        NullValue,
        ScalarValue,
        ArrayValue
    };

    public class NameValuePair
    {
        private ValueType _valueType;
        private string _name = "";
        private string _value = "";
        private List<string> _values = new List<string>();

        public NameValuePair(string name, string value)
        {
            _name = name;
            _value = value;
            _values.Add(value);
            _valueType = ValueType.ScalarValue;

        }

        public NameValuePair(string name, List<string> values)
        {
            _name = name;

            if (values.Count == 0)
            {
                _value = "";
                _values.Add("");
                _valueType = ValueType.NullValue;

            }
            else if (values.Count == 1)
            {
                _value = values[0];
                _values.Add(_value);
                _valueType = ValueType.ScalarValue;
            }
            else
            {
                _value = values[0];
                _values = values;
                _valueType = ValueType.ArrayValue;

            }

        }

        public NameValuePair(string name, string[] values)
        {
            _name = name;

            if (values == null)
            {
                _value = "";
                _values.Add("");
                _valueType = ValueType.NullValue;
            }
            else if (values != null && values.Length == 1)
            {
                _value = values[0];
                _values.Add(_value);
                _valueType = ValueType.ScalarValue;
            }
            else if (values != null && values.Length > 1)
            {
                _value = values[0];
                
                foreach (string s in values)
                {
                    _values.Add(s);
                }

                _valueType = ValueType.ArrayValue;
            }

        }
        
        public string Name
        {
            get { return _name; }
        }

        public string Value
        {
            get { return _value; }
        }

        public List<string> Values
        {
            get { return _values; }
        }

        public string[] ValuesAsArray
        {
            get { return _values.ToArray(); }
        }

        public ValueType ValueType
        {
            get { return _valueType; }
            set { _valueType = value; }
        }

        public string ValuesAsString
        {
            get
            {
                return string.Join(", ", _values.ToArray());
            }
        }

        /// <summary>
        /// Replaces one occurrence of oldValue with newValue, both in the scalar value and
        /// in the values list. Used by the file-upload logic when the database (e.g.
        /// apex_util.set_blob) generates the stored file name itself: the parameter value
        /// passed to the target procedure must then be updated to match the name the
        /// database actually stored the file under.
        /// </summary>
        public void ReplaceValue(string oldValue, string newValue)
        {
            if (_value == oldValue)
            {
                _value = newValue;
            }

            for (int i = 0; i < _values.Count; i++)
            {
                if (_values[i] == oldValue)
                {
                    _values[i] = newValue;
                    break;
                }
            }
        }

        public string DebugValue
        {
            get
            {
                if (_valueType == ValueType.ScalarValue)
                {
                    return Value;
                }
                else
                {
                    return ValuesAsString;
                }
            }
        }
    
    }
}
