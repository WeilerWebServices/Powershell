using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Microsoft.PowerShell.Cmdletization.OData
{
    /// <summary>
    /// ODataQueryBuilder represents OData specific Query builder used for building Object model queries.
    /// </summary>
    public class ODataQueryBuilder : QueryBuilder
    {
        /// <summary>
        /// Stores keys that were passed to the cmdlet.
        /// </summary>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists")]
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public List<Tuple<string, object>> Keys { get; set; }
        internal string OrderByQuery = null;
        internal string TopQuery = null;
        internal string IncludeTotalResponseCountQuery = null;
        internal string SelectQuery = null;
        internal string SkipQuery = null;
        internal string FilterQuery = null;
        internal string ConcatinationOperator = "&";

        /// <summary>
        /// Holds name of referred resource for Get association cmdlet
        /// </summary>
        internal string ReferredResource { get; set; }
        
        /// <summary>
        /// 
        /// </summary>
        public ODataQueryBuilder()
        {
            Keys = new List<Tuple<string, object>>();
        }

        /// <summary>
        /// Adds key properties. Each property has a single value (first).
        /// </summary>
        /// <param name="propertyName">Name of the property (key)</param>
        /// <param name="allowedPropertyValues">Property value</param>
        /// <param name="wildcardsEnabled">ignored</param>
        /// <param name="behaviorOnNoMatch">ignored</param>
        public override void FilterByProperty(string propertyName, IEnumerable allowedPropertyValues, bool wildcardsEnabled, BehaviorOnNoMatch behaviorOnNoMatch)
        {
            if (propertyName == null) throw new ArgumentNullException("propertyName");
            if (allowedPropertyValues == null) throw new ArgumentNullException("allowedPropertyValues");

            // association properties are in the format ReferredResource:propertyName:Key
            var propertyNameSplit = propertyName.Split(':');

            string basePropertyName;

            // regular Get
            if (propertyNameSplit.Length == 1)
            {
                basePropertyName = propertyName;
                
            }
            // Check if its a Query Option.
            else if (propertyNameSplit.Length == 2 && string.Equals(propertyNameSplit[0], "QueryOption", StringComparison.OrdinalIgnoreCase))
            {
                basePropertyName = propertyNameSplit[1];
            }
            // association Get
            else if (propertyNameSplit.Length == 3)
            {
                ReferredResource = propertyNameSplit[0];
                if (propertyNameSplit[2] != "Key")
                {
                    throw new InvalidOperationException(String.Format(CultureInfo.InvariantCulture, Resources.IncorrectPropertyNameFormat, propertyName));
                }

                basePropertyName = propertyNameSplit[1];
            }
            else
            {
                throw new InvalidOperationException(String.Format(CultureInfo.InvariantCulture, Resources.IncorrectPropertyNameFormat, propertyName));
            }

            var enumerator = allowedPropertyValues.GetEnumerator();
            enumerator.MoveNext();
            object value = enumerator.Current;

            if (value != null)
            {
                // Select is the only Query parameter for which filtering is 
                // supported on mutliple property names.
                // The Property names selected go over the wire as strings.
                if (string.Compare(basePropertyName, "Select", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    StringBuilder selectQueryValues = new StringBuilder(value.ToString());
                    while (enumerator.MoveNext())
                    {
                        value = enumerator.Current;
                        if (value != null)
                        {
                            selectQueryValues.Append(",");
                            selectQueryValues.Append(value.ToString());
                        }
                    }

                    value = selectQueryValues.ToString();
                }

                bool isQueryTypeParameter = TryProcessQueryProperty(basePropertyName, value);

                if (!isQueryTypeParameter)
                {
                    Keys.Add(new Tuple<string, object>(basePropertyName, value));
                }
            }
        }

        /// <summary>
        /// TryProcessQueryProperty is a helper method used to keep track of different query options (ex: Top, Skip, OrderBy, Filter) specified during cmdlet invocation.
        /// </summary>
        /// <param name="propertyName">Property Name.</param>
        /// <param name="value">Property Value.</param>
        /// <returns>True if the input propery is one of the supported query properties, else False is returned.</returns>
        private bool TryProcessQueryProperty(string propertyName, object value)
        {
            bool result = false;

            if(!string.IsNullOrEmpty(propertyName))
            {
                switch(propertyName)
                {
                    case "Top": 
                        TopQuery = "$top=" + value;
                        result = true;
                        break;
                    case "IncludeTotalResponseCount":
                        IncludeTotalResponseCountQuery = "$inlinecount=" + "allpages";
                        result = true;
                        break;
                    case "Select":
                        SelectQuery = "$select=" + value;
                        result = true;
                        break;
                    case "Skip":
                        SkipQuery = "$skip=" + value;
                        result = true;
                        break;
                    case "OrderBy":
                        OrderByQuery = "$orderby=" + value;
                        result = true;
                        break;
                    case "Filter":
                        FilterQuery = "$filter=" + value;
                        result = true;
                        break;
                }
            }

            return result;
        }
    }
}
