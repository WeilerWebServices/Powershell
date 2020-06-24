using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Management.Automation;
using System.Text;

namespace Microsoft.PowerShell.Cmdletization.OData
{
    /// <summary>
    /// This is a CDXML Adapter implementation for OData V4.
    /// </summary>
    public class ODataV4CmdletAdapter : ODataCmdletAdapter
    {
        /// <summary>
        /// If no format is specified by the user, we will ask for the response to be in this format
        /// </summary>
        protected const string DefaultResponseFormatMimeType = "application/json";

        /// <summary>
        /// Accept header name
        /// </summary>
        protected const string AcceptHeader = "Accept";

        /// <summary>
        /// OData-Version header name
        /// </summary>
        protected const string ODataVersionHeader = "OData-Version";

        /// <summary>
        /// Default ODataVersion value
        /// </summary>
        protected const string DefaultODataVersion = "4.0";

        /// <summary>
        /// Internal headers field
        /// </summary>
        protected Hashtable headers = new Hashtable();

        /// <summary>
        /// Gets or Sets the Headers that needs to be 
        /// used by the REST protocol while interacting with the web service. 
        /// It Specifies a collection of the name/value pairs that make up the HTTP headers.
        /// </summary>
        public override Hashtable Headers
        {
            get
            {
                Hashtable result = AddAcceptHeaderIfMissing(this.headers);
                result = AddODataVersionHeaderIfMissing(result);
                return result;
            }
            set
            {
                this.headers = value;
            }
        }

        /// <summary>
        /// Builds Uri based on query
        /// </summary>
        /// <param name="originalUri"></param>
        /// <param name="connectionUri"></param>
        /// <param name="referenceByKeys"></param>
        /// <param name="referredResource"></param>
        /// <returns>Uri</returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        [SuppressMessage("Microsoft.Design", "CA1054:UriParametersShouldNotBeStrings", MessageId = "0#")]
        protected override Tuple<string, string> BuildBaseUri(string originalUri, Uri connectionUri, List<Tuple<string, object>> referenceByKeys, string referredResource)
        {
            Debug.Assert((originalUri != null), "Uri is pointing to NULL in ODataV4CmdletAdapter.ProcessRecord");

            return new Tuple<string, string>(originalUri, referredResource);
        }

        /// <summary>
        /// This is implementation of Action cmdlet
        /// </summary>
        /// <param name="filteredParameters"></param>
        /// <param name="isForceParameterSpecified"></param>
        /// <param name="keys"></param>
        /// <param name="referenceByKeys"></param>
        /// <param name="verbSplit"></param>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        protected override void ProcessActionRecord(Dictionary<string, object> filteredParameters, bool isForceParameterSpecified, List<Tuple<string, object>> keys, string referenceByKeys, string[] verbSplit)
        {
            Debug.Assert((filteredParameters != null), "filteredParameters is pointing to NULL in ODataV4CmdletAdapter.ProcessActionRecored");
            Debug.Assert((keys != null), "keys is pointing to NULL in ODataV4CmdletAdapter.ProcessActionRecored");
            Debug.Assert((verbSplit != null), "verbSplit is pointing to NULL in ODataV4CmdletAdapter.ProcessActionRecored");
            Debug.Assert((verbSplit.Length >= 3), "verbSplit array has wrong number of items ODataV4CmdletAdapter.ProcessActionRecored");

            string uri;
            Uri finalUri;

            if (String.IsNullOrEmpty(referenceByKeys))
            {
                var otherParameters = GetNonKeys(filteredParameters);
                uri = BuildODataUriParameters(GetCustomUriHelper(this.ClassName, this.ConnectionUri, keys) + verbSplit[1], otherParameters);
                finalUri = BuildODataUri(uri, new Dictionary<string, string>() { { "$format", DefaultResponseFormat } });
            }
            else
            {
                finalUri = BuildODataUri(GetCustomUriHelper(this.ClassName, this.ConnectionUri, keys) + referenceByKeys + "/" + verbSplit[1], new Dictionary<string, string>() { { "$format", DefaultResponseFormat } });
            }

            if (ShouldProcessHelper(this.Cmdlet, finalUri, true, isForceParameterSpecified))
            {
                var otherParameters = GetNonKeys(filteredParameters);
                
                if (!PrivateData.ContainsKey("EntityTypeName"))
                {
                    this.ActionEntityType = verbSplit[2];
                }

                GetActionCmdlet(this.Cmdlet, finalUri, otherParameters);
            }
        }

        /// <summary>
        /// Returns if the return value is a single instance
        /// </summary>
        /// <param name="referencedByKeys">Keys string</param>
        /// <returns>True if result is a single instance. False otherwise</returns>
        protected override bool IsSingleInstance(string referencedByKeys)
        {
            string isSingleton = "False";
            bool isSingletonBool = false;

            return (!String.IsNullOrEmpty(referencedByKeys) ||
                (true == PrivateData.TryGetValue("IsSingleton", out isSingleton) &&
                !String.IsNullOrEmpty(isSingleton) &&
                bool.TryParse(isSingleton, out isSingletonBool) &&
                isSingletonBool));
        }

        /// <summary>
        /// Gets OData resource reference by keys.
        /// We support two key formats in uri resource path:
        /// 1. When key is embedded. For example: "(Id=7,Name='apple')" for ~/.svc/Product(Id=7,Name='apple')
        /// 2. When key is separate. For example: "/7" for ~/.svc/Product/7
        /// </summary>
        /// <param name="keys">Referred keys.</param>
        /// <returns>Reference by keys string for current resource.</returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists")]
        public override string GetODataReferenceByKeys(List<Tuple<string, object>> keys)
        {
            if (keys == null) throw new ArgumentNullException("keys");
            string result = String.Empty;

            if (keys.Count == 0)
            {
                return result;
            }

            // EmbeddedKey is the default uri resource path construction convention
            UriResourcePathKeyFormat format = UriResourcePathKeyFormat.EmbeddedKey;
            string UriResourcePathKeyFormatStr = String.Empty;

            if (PrivateData != null &&
                true == PrivateData.TryGetValue("UriResourcePathKeyFormat", out UriResourcePathKeyFormatStr) &&
                !UriResourcePathKeyFormatStr.Equals(format.ToString()))
            {
                format = UriResourcePathKeyFormat.SeparateKey;
            }

            if (format == UriResourcePathKeyFormat.SeparateKey)
            {
                foreach (var key in keys)
                {                
                    // Redfish Service (ODataV4) services URI construction convention supports format 
                    // where the key value is specified, but not the key name.
                    result += "/" + key.Item2;
                }
            }
            else
            {
                result = base.GetODataReferenceByKeys(keys);
            }

            return FormatODataReferenceByKeys(result, format);
        }

        /// <summary>
        /// Formats part of the URI, which specifies all key and value pairs
        /// </summary>
        /// <param name="referenceByKeys">part of the URI, which specifies all key and value pairs</param>
        /// <param name="format">Format of the key name/value pair in the uri</param>
        /// <returns>formated key/value pair string</returns>
        protected override string FormatODataReferenceByKeys(string referenceByKeys, UriResourcePathKeyFormat format)
        {
            if (format == UriResourcePathKeyFormat.EmbeddedKey && !referenceByKeys.StartsWith("(", StringComparison.Ordinal) && !referenceByKeys.EndsWith(")", StringComparison.Ordinal))
            {
                return "(" + referenceByKeys + ")";
            }
            else
            {
                return referenceByKeys;
            }
        }

        /// <summary>
        /// Adds additional parameters to OData uri
        /// </summary>
        /// <param name="uri">Base uri</param>
        /// <param name="uriParameters">Uri parameters</param>
        /// <returns>Full uri</returns>
        private string BuildODataUriParameters(string uri, List<Tuple<string, object>> uriParameters)
        {
            StringBuilder sb = new StringBuilder(uri);

            string delimiter = ",";

            if (uriParameters != null && uriParameters.Count > 0)
            {
                sb.Append("(");
                foreach (var queryParameter in uriParameters)
                {
                    sb.Append(queryParameter.Item1 + "=" + queryParameter.Item2);
                    sb.Append(delimiter);
                }
                // Remove last delimieter
                sb.Remove(sb.Length - 1, 1);
                sb.Append(")");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Adds additional query parameters to uri
        /// </summary>
        /// <param name="uri">Base uri</param>
        /// <param name="queryParameters">Query parameters</param>
        /// <returns>Full uri</returns>
        protected override Uri BuildODataUri(string uri, Dictionary<string, string> queryParameters)
        {
            // implementation is the same as base ODataCmdletAdapter:BuildODataUri
            // but according to bug 6793020 we should not include "$format"

            StringBuilder sb = new StringBuilder(uri);

            string delimiter = "?";

            foreach (var queryParameter in queryParameters)
            {
                if (!queryParameter.Key.Equals("$format", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(delimiter);
                    sb.Append(queryParameter.Key + "=" + queryParameter.Value);
                }
            }

            return (new Uri(sb.ToString()));
        }

        /// <summary>
        /// This method adds the Format option in url
        /// </summary>
        /// <param name="uri">url</param>
        /// <returns>url after adding format json</returns>
        protected override Uri AppendFormatOption(Uri uri)
        {
            // do not append $format according to bug 6793020
            return uri;
        }

        /// <summary>
        /// Helper method that returns Type of returned object
        /// </summary>
        /// <returns></returns>
        protected override Type GetRequestedType(PSCmdlet cmdlet)
        {
            Type requestedType = null;

            if (!String.IsNullOrEmpty(this.ActionEntityType))
            {
                requestedType = LanguagePrimitives.ConvertTo<Type>(this.ActionEntityType);
            }
            else
            {
                PrivateDateValidationHelper(cmdlet, "EntityTypeName");
                requestedType = LanguagePrimitives.ConvertTo<Type>(PrivateData["EntityTypeName"]);
            }

            return requestedType;
        }

        /// <summary>
        /// Adds Accept if it is missing from the list of headers
        /// </summary>
        /// <param name="headers">List of headers to search</param>
        /// <returns>Header list with added Accept</returns>
        protected Hashtable AddAcceptHeaderIfMissing(Hashtable headers)
        {
            return AddHeaderIfMissing(headers, AcceptHeader, DefaultResponseFormatMimeType);
        }

        /// <summary>
        /// Adds OData-Version if it is missing from the list of headers
        /// </summary>
        /// <param name="headers">List of headers to search</param>
        /// <returns>Header list with added OData-Version</returns>
        protected Hashtable AddODataVersionHeaderIfMissing(Hashtable headers)
        {
            return AddHeaderIfMissing(headers, ODataVersionHeader, DefaultODataVersion);
        }

        /// <summary>
        /// Adds a specific header if it is missing from the list of headers
        /// </summary>
        /// <param name="headers">List of headers to search</param>
        /// <param name="headerName">Name of header to add</param>
        /// <param name="headerValue">Value of header to add</param>
        /// <returns>Updated header list</returns>
        protected Hashtable AddHeaderIfMissing(Hashtable headers, string headerName, string headerValue)
        {
            Hashtable result = new Hashtable();
            if (headers != null)
            {
                // need to do case-insensitive search since these items may be set by the user
                foreach (string header in headers.Keys)
                {
                    if (header.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                    {
                        return headers; // specified header already exists - just return current headers
                    }
                }
                if (headers.Count > 0) // in majority of cases users won't be using custom headers, so this small optimization will save on cloning empty HT
                {
                    result = (Hashtable)headers.Clone();
                }
            }
            // if we are here - we need to add the specified header
            result.Add(headerName, headerValue);
            return result;
        }
    }
}
