using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Management.Automation;
using System.Text;

namespace Microsoft.PowerShell.Cmdletization.OData
{
    /// <summary>
    /// This is a CDXML Adapter implementation for Redfish.
    /// </summary>
    public class RedfishCmdletAdapter : ODataV4CmdletAdapter
    {
        /// <summary>
        /// Gets or Sets the Headers that needs to be 
        /// used by the REST protocol while interacting with the web service. 
        /// It Specifies a collection of the name/value pairs that make up the HTTP headers.
        /// </summary>
        [Parameter]
        [ValidateNotNull]
        public override Hashtable Headers
        {
            get
            {
                return base.Headers;
            }
            set
            {
                base.Headers = value;
            }
        }

        /// <summary>
        /// This is implementation for Get cmdlet: all instances, single instance, Get on associations
        /// </summary>
        /// <param name="query"></param>
        public override void ProcessRecord(QueryBuilder query)
        {
            ODataQueryBuilder odataQuery = query as ODataQueryBuilder;

            if (!this.PassInnerException.IsPresent)
            {
                string passInnerExceptionKey = "PassInnerException";
                if (PrivateData.ContainsKey(passInnerExceptionKey))
                {
                    string PassInnerExceptionValue = PrivateData[passInnerExceptionKey];
                    this.PassInnerException = PassInnerExceptionValue.Equals("True", StringComparison.OrdinalIgnoreCase);
                }
            }

            string parentTypeName = this.Cmdlet.ParameterSetName;
            string privateDataEntryName = "NavigationLink" + parentTypeName;
            if (PrivateData.ContainsKey(privateDataEntryName))
            {
                string parent = PrivateData[privateDataEntryName];

                string[] parentParts = parent.Split('|');
                string parentPropertyName = parentParts[2];

                System.Management.Automation.PSObject[] argumentObjects = (System.Management.Automation.PSObject[])this.Cmdlet.MyInvocation.BoundParameters[parentTypeName];

                foreach (System.Management.Automation.PSObject parentObject in argumentObjects)
                {
                    bool isCollection = parentParts[parentParts.Length - 1].Equals("Collection", StringComparison.OrdinalIgnoreCase);

                    if (isCollection)
                    {
                        object[] parentPropertyObjectArray = (object[])parentObject.Properties[parentPropertyName].Value;
                        foreach (System.Management.Automation.PSObject parentPropertyObject in parentPropertyObjectArray)
                        {
                            string odataId = (string)parentPropertyObject.Properties["@odata.id"].Value;
                            string hostUri = GetCustomUriHelper(this.ClassName, this.ConnectionUri, odataQuery.Keys);
                            UriBuilder uriBuilder = new UriBuilder(hostUri);
                            uriBuilder.Path = odataId;
                            GetCmdlet(this.Cmdlet, uriBuilder.Uri, true, odataQuery);
                        }
                    }
                    else
                    {
                        System.Management.Automation.PSObject parentPropertyObject = (System.Management.Automation.PSObject)parentObject.Properties[parentPropertyName].Value;
                        string odataId = (string)parentPropertyObject.Properties["@odata.id"].Value;
                        string hostUri = GetCustomUriHelper(this.ClassName, this.ConnectionUri, odataQuery.Keys);
                        UriBuilder uriBuilder = new UriBuilder(hostUri);
                        uriBuilder.Path = odataId;
                        GetCmdlet(this.Cmdlet, uriBuilder.Uri, true, odataQuery);
                    }
                }
            }
            else
            {
                base.ProcessRecord(query);
            }
        }

        /// <summary>
        /// This is implementation of all other cmdlets: CUD, actions
        /// </summary>
        /// <param name="methodInvocationInfo"></param>
        public override void ProcessRecord(MethodInvocationInfo methodInvocationInfo)
        {
            if (methodInvocationInfo == null) throw new ArgumentNullException("methodInvocationInfo");

            if (!this.PassInnerException.IsPresent)
            {
                string passInnerExceptionKey = "PassInnerException";
                if (PrivateData.ContainsKey(passInnerExceptionKey))
                {
                    string PassInnerExceptionValue = PrivateData[passInnerExceptionKey];
                    this.PassInnerException = PassInnerExceptionValue.Equals("True", StringComparison.OrdinalIgnoreCase);
                }
            }

            List<Tuple<string, object>> parameters = new List<Tuple<string, object>>();

            string odataId = string.Empty;
            if (methodInvocationInfo.MethodName.Equals("Delete", StringComparison.OrdinalIgnoreCase))
            {
                MethodParameter parameter = methodInvocationInfo.Parameters[0];
                if (parameter.Name.Equals("OdataId", StringComparison.OrdinalIgnoreCase))
                {
                    odataId = (string)parameter.Value;
                }
                else if (parameter.Name.Equals("Resource", StringComparison.OrdinalIgnoreCase))
                {
                    odataId = (string)((System.Management.Automation.PSObject)parameter.Value).Properties["@odata.id"].Value;
                }
            }
            else if (methodInvocationInfo.MethodName.StartsWith("Action", StringComparison.OrdinalIgnoreCase))
            {
                string[] actionParts = methodInvocationInfo.MethodName.Split(':');
                string actionName = actionParts[1];
                string actionTargetParameterName = string.Empty;

                foreach (string actionTargetString in PrivateData["ActionTargets"].Split('|'))
                {
                    if (actionTargetString.StartsWith(actionName, StringComparison.OrdinalIgnoreCase))
                    {
                        actionTargetParameterName = actionTargetString.Split('=')[1];
                    }
                }

                foreach (MethodParameter parameter in methodInvocationInfo.Parameters)
                {
                    if (parameter.Name.Equals("OdataId", StringComparison.OrdinalIgnoreCase))
                    {
                        odataId = (string)parameter.Value;
                        continue;
                    }

                    if (parameter.Name.Equals(actionTargetParameterName, StringComparison.OrdinalIgnoreCase))
                    {
                        object actions = ((System.Management.Automation.PSObject)parameter.Value).Properties["Actions"].Value;
                        object action = ((System.Management.Automation.PSObject)actions).Properties["#" + actionName].Value;
                        odataId = (string)(((System.Management.Automation.PSObject)action).Properties["target"].Value);
                        continue;
                    }

                    if (parameter.IsValuePresent)
                    {
                        parameters.Add(new Tuple<string, object>(parameter.Name, parameter.Value));
                    }
                }

            }
            else
            {
                foreach (MethodParameter parameter in methodInvocationInfo.Parameters)
                {
                    if (parameter.Name.Equals("OdataId", StringComparison.OrdinalIgnoreCase))
                    {
                        odataId = (string)parameter.Value;
                        continue;
                    }

                    if (parameter.IsValuePresent)
                    {
                        parameters.Add(new Tuple<string, object>(parameter.Name, parameter.Value));
                    }
                }
            }

            string hostUri = GetCustomUriHelper(this.ClassName, this.ConnectionUri, null);
            UriBuilder uriBuilder = new UriBuilder(hostUri);
            uriBuilder.Path = odataId;

            string body = string.Empty;
            System.Management.Automation.PowerShell ps;
            switch (methodInvocationInfo.MethodName)
            {
                case "Create":
                    body = SerializeParameters(this.Cmdlet, null, parameters, false);
                    ps = CreateCmdlet(this.Cmdlet, null, null, null, body);
                    break;
                case "Update":
                    body = SerializeParameters(this.Cmdlet, null, parameters, false);
                    ps = UpdateCmdlet(this.Cmdlet, null, null, null, body);
                    break;
                case "Delete":
                    ps = DeleteCmdlet(this.Cmdlet, null, null);
                    break;
                default:
                    {
                        if (methodInvocationInfo.MethodName.StartsWith("Action", StringComparison.OrdinalIgnoreCase))
                        {
                            GetActionCmdlet(this.Cmdlet, uriBuilder.Uri, parameters);
                            return;
                        }
                        else
                        {
                            throw new NotImplementedException();
                        }
                    }
            }

            InvokePSPassStreamsToPSCmdlet(ps, this.Cmdlet, uriBuilder.Uri);
            if (ps != null) ps.Dispose();
        }
        /*
        /// <summary>
        /// Serializes parameters into OData-formatted json
        /// </summary>
        /// <param name="cmdlet">PS Cmdlet under execution.</param>
        /// <param name="parameters">Parameters that need to be serialized</param>
        /// <returns>Json string</returns>
        protected virtual string SerializeParameters(PSCmdlet cmdlet, List<Tuple<string, object>> parameters)
        {
            var returnObject = new Dictionary<string, object>();

            returnObject = AddParameterToDictionary(returnObject, "AssetTag", "Test1");
            

            JavaScriptSerializer jss = new JavaScriptSerializer();
            return jss.Serialize(returnObject);
        }
        */

        /// <summary>
        /// Build Uri based on dynamic value from the object
        /// </summary>
        /// <param name="uri">Base uri</param>
        /// <param name="queryParameters">Query parameters</param>
        /// <returns>Full uri</returns>
        [SuppressMessage("Microsoft.Design", "CA1054:UriParametersShouldNotBeStrings", Justification = "By Design")]
        protected override Uri BuildODataUri(string uri, Dictionary<string, string> queryParameters)
        {
            System.Management.Automation.PSObject argumentObjects = (System.Management.Automation.PSObject)this.Cmdlet.MyInvocation.BoundParameters["Value"];
            System.Management.Automation.PSObject parentPropertyObject = argumentObjects;

            string odataId = (string)parentPropertyObject.Properties["@odata.id"].Value;
            string hostUri = GetCustomUriHelper(this.ClassName, this.ConnectionUri, null);
            UriBuilder uriBuilder = new UriBuilder(hostUri);
            uriBuilder.Path = odataId;
            
            return uriBuilder.Uri;
        }

        /// <summary>
        /// GetCustomUriHelper is a helper method used to detect/construct the connection Uri to be used for the Redfish endpoint.
        /// </summary>
        /// <param name="defaultUri">
        /// Default ConnectionUri used to connect to OData Endpoint.
        /// </param>
        /// <param name="connectionUri">
        /// User specified Base connection uri.
        /// </param>
        /// <param name="keys">The keys passed with cmdlet</param>
        /// <returns>
        /// The Connection Uri to be used to connect to OData Endpoint.
        /// </returns>
        public override string GetCustomUriHelper(string defaultUri, Uri connectionUri, List<Tuple<string, object>> keys)
        {
            if (connectionUri == null)
            {
                return defaultUri;
            }

            return connectionUri.ToString();
        }

        /// <summary>
        /// TryUpdateQueryOptions is a helper function used to update the query options (i.e., Filters, Top, Skip, OrderBy) to the base Uri.
        /// </summary>
        /// <param name="uri">Base Uri.</param>
        /// <param name="adapterQueryBuilder">Adapter specific Query Builder</param>
        /// <returns>If Query options is specified, Base Uri would be appended with the Query options or else the Base Uri would be returned.</returns>
        protected override Uri TryUpdateQueryOptions(Uri uri, ODataQueryBuilder adapterQueryBuilder)
        {
            Debug.Assert(((uri != null) && (uri.AbsoluteUri != null)), "Uri is pointing to NULL in ODataCmdletAdapter.TryUpdateQueryOptions");

            StringBuilder queryOperation = new StringBuilder();
            List<string> queryOptions = new List<string>();

            if (adapterQueryBuilder != null)
            {
                if (!string.IsNullOrEmpty(adapterQueryBuilder.FilterQuery))
                {
                    queryOptions.Add(adapterQueryBuilder.FilterQuery);
                }

                if (!string.IsNullOrEmpty(adapterQueryBuilder.OrderByQuery))
                {
                    queryOptions.Add(adapterQueryBuilder.OrderByQuery);
                }

                if (!string.IsNullOrEmpty(adapterQueryBuilder.SelectQuery))
                {
                    queryOptions.Add(adapterQueryBuilder.SelectQuery);
                }

                if (!string.IsNullOrEmpty(adapterQueryBuilder.IncludeTotalResponseCountQuery))
                {
                    queryOptions.Add(adapterQueryBuilder.IncludeTotalResponseCountQuery);
                }

                if (!string.IsNullOrEmpty(adapterQueryBuilder.SkipQuery))
                {
                    queryOptions.Add(adapterQueryBuilder.SkipQuery);
                }

                if (!string.IsNullOrEmpty(adapterQueryBuilder.TopQuery))
                {
                    queryOptions.Add(adapterQueryBuilder.TopQuery);
                }

                if (queryOptions.Count > 0)
                {
                    queryOperation.Append(queryOptions[0]);
                    for (int index = 1; index < queryOptions.Count; index++)
                    {
                        queryOperation.Append(adapterQueryBuilder.ConcatinationOperator);
                        queryOperation.Append(queryOptions[index]);
                    }

                    return (new Uri(uri.AbsoluteUri + "?" + queryOperation.ToString()));
                }
            }

            return uri;
        }

        /// <summary>
        /// This is an implementation of Create Operation.
        /// </summary>
        /// <param name="cmdlet">
        /// Cmdlet Object to pass data to PowerShell.
        /// </param>
        /// <param name="uri">
        /// Full Resource Uri.
        /// </param>
        /// <param name="keys">
        /// List of Key Properties.
        /// </param>
        /// <param name="parameters">
        /// Collection of Parameters.
        /// </param>
        /// <param name="body">json content</param>
        protected virtual System.Management.Automation.PowerShell CreateCmdlet(PSCmdlet cmdlet, Uri uri, List<Tuple<string, object>> keys, Dictionary<string, object> parameters, string body)
        {
            var ps = System.Management.Automation.PowerShell.Create();
            string action;
            if (PrivateData.TryGetValue("CreateRequestMethod", out action) == false) Debug.Assert(false, "CreateRequestMethod not present in PrivateData");
            action = action.ToUpperInvariant();
            ps.AddCommand(Command).AddParameter("Method", action).AddParameter("Body", body).AddParameter("Verbose").AddParameter("Debug");
            SetContentType(ps);
            return ps;
        }
    }
}
