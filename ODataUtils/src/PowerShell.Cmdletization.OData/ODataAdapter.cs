//-----------------------------------------------------------------------
// <copyright file="ODataAdapter.cs" company="Microsoft Corporation">
//     Copyright (C) 2014 Microsoft Corporation
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;

[assembly: SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "Cmdletization")]
[assembly: SuppressMessage("Microsoft.Design", "CA2210:AssembliesShouldHaveValidStrongNames")]
[assembly: SuppressMessage("Microsoft.Design", "CA1014:MarkAssembliesWithClsCompliant")]
[assembly: SuppressMessage("Microsoft.Design", "CA1020:AvoidNamespacesWithFewTypes", Scope = "namespace", Target = "Microsoft.PowerShell.Cmdletization.OData")]

namespace Microsoft.PowerShell.Cmdletization.OData
{
    /// <summary>
    /// This is a CDXML Adapter implementation for OData
    /// </summary>
    public class ODataCmdletAdapter : CmdletAdapter<object>
    {
        /// <summary>
        /// Gets or Sets the Uri of the OData endpoint.
        /// If this parameter is specified during the cmdlet invocation, 
        /// the specfied Uri would be used instead of the default one. 
        /// </summary>
        [Parameter]
        [ValidateNotNullOrEmpty]
        public virtual Uri ConnectionUri { get; set; }

        /// <summary>
        /// Gets or Sets the CertificateThumbprint that needs to be 
        /// used by the REST protocol for authentication while interacting with the web service.
        /// </summary>
        [Parameter]
        [ValidateNotNullOrEmpty]
        public string CertificateThumbprint { get; set; }

        /// <summary>
        /// Gets or Sets the PSCredential that needs to be 
        /// used by the REST protocol for authentication while interacting with the web service.
        /// </summary>
        [Parameter]
        [Credential()]
        public PSCredential Credential { get; set; }

        /// <summary>
        /// Gets or Sets the Headers that needs to be 
        /// used by the REST protocol while interacting with the web service. 
        /// It Specifies a collection of the name/value pairs that make up the HTTP headers.
        /// The Hearders can also contain details about custom authentication to be used wile 
        /// interacting with the web service. 
        /// </summary>
        [Parameter]
        [ValidateNotNull]
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public virtual Hashtable Headers { get; set; }

        /// <summary>
        /// Gets or Sets the boolean value to indicate if un secured connection to the server
        /// side endpoint is allowed or not. By default unsecure connection to the server side 
        /// endpoint is not allowed which can be overwridden by specifying this switch parameter 
        /// during client side proxy cmdlet invocation.
        /// </summary>
        [Parameter]
        public virtual SwitchParameter AllowUnsecureConnection { get; set; }

        /// <summary>
        /// Gets or Sets the boolean value to indicate if additional data from the server side
        /// has to be included or not while being written to the client side.
        /// The server side may occassionally send addional data that is not mention in the metadata,
        /// The client side user has an option to filter or allow the addional details 
        /// being sent during the client side proxy invocation.
        /// </summary>
        [Parameter]
        public virtual SwitchParameter AllowAdditionalData { get; set; }

        /// <summary>
        /// If True, do not wrap exceptions coming from bottom layers.
        /// </summary>
        [Parameter]
        public virtual SwitchParameter PassInnerException { get; set; }
#if CORECLR
        /// <summary> 
        /// Gets or Sets the boolean value to indicate if client should skip validation of server certificate.
        /// </summary> 
        [Parameter] 
        public virtual SwitchParameter SkipCertificateCheck { get; set; }
#endif
        /// <summary>
        /// If no format is specified by the user, we will ask for the response to be in this format
        /// </summary>
        protected const string DefaultResponseFormat = "json";

        /// <summary>
        /// ConcurrentQueue to keep track of all the stream messages and non terminating error records 
        /// written from the server side endpoint. These stream messages and non terminating error recores 
        /// would be channeled to the client side in the order they appear.
        /// </summary>
        private ConcurrentQueue<object> endPointRecordQueue = new ConcurrentQueue<object>();

        /// <summary>
        /// PSDataCollection to keep track of result objects from command execution.
        /// </summary>
        private PSDataCollection<PSObject> output = new PSDataCollection<PSObject>();

        /// <summary>
        /// This script helps in channeling additional information from the server through Information channel.
        /// </summary>
        private string additionalInfoScript = @"
$propertyNames = $responseObjects | Get-Member -MemberType NoteProperty | Select-Object -Property Name
$additionalInfo = @{}
foreach($currentPropertyName in $propertyNames)
{
    if($currentPropertyName.Name -ne $null -and $currentPropertyName.Name -ne 'value')
    {
       $propValue = $responseObjects | ForEach-Object -MemberName $currentPropertyName.Name 
       if(-not $additionalInfo.ContainsKey($currentPropertyName.Name))
       {
            $additionalInfo.Add($currentPropertyName.Name, $propValue)
       }
    }
}
if($additionalInfo.Count -gt 0)
{
    Write-Information -MessageData $additionalInfo -Tags AdditionalInfo
}
$responseObjects | ForEach-Object -MemberName value
";

        /// <summary>
        /// The format of the key in resource path part of URI
        /// </summary>
        protected enum UriResourcePathKeyFormat
        {
            /// <summary>
            /// Default. Key is embedded (i.e., webservice.svc/ResourceName(ResourceKey=ResourceValue))
            /// </summary>
            EmbeddedKey,

            /// <summary>
            /// Key forms separate part of URI resource path (i.e., webservice.svc/ResourceName/ResourceId)
            /// </summary>
            SeparateKey
        }

        /// <summary>
        /// Gets or Sets the returned type of service-level Action. 
        /// </summary>
        protected string ActionEntityType { get; set; }

        /// <summary>
        /// Powershell command to be executed for REST request
        /// </summary>
        protected virtual string Command
        {
            get { return "Invoke-RestMethod"; }
        }

        /// <summary>
        /// Error string to be used if unsecure uri is specified with Connectionuri
        /// </summary>
        protected virtual string AllowUnsecureConnectionErrorString
        {
            get { return Resources.AllowUnsecureConnectionMessage; }
        }

        /// <summary>
        /// Error string to be used if data from server cannot be converted to entity type
        /// </summary>
        protected virtual string AllowAdditionalDataErrorString
        {
            get { return Resources.TypeCastFailureAdditionalMembers; }
        }

        /// <summary>
        /// Should process message for CRUD operations
        /// </summary>
        protected virtual string ShouldProcessCrudMessage
        {
            get { return Resources.ShouldProcessCrudMessage; }
        }

        /// <summary>
        /// Should continue message for CRUD operations
        /// </summary>
        protected virtual string ShouldContinueCrudMessage
        {
            get { return Resources.ShouldContinueCrudMessage; }
        }

        /// <summary>
        /// Retuns an instance of new OData query builder.
        /// </summary>
        /// <returns></returns>
        public override QueryBuilder GetQueryBuilder()
        {
            return new ODataQueryBuilder();
        }

        /// <summary>
        /// This is implementation for Get cmdlet: all instances, single instance, Get on associations
        /// </summary>
        /// <param name="query"></param>
        public override void ProcessRecord(QueryBuilder query)
        {
            if (query == null) throw new ArgumentNullException("query");
            ODataQueryBuilder odataQuery = query as ODataQueryBuilder;

            if (odataQuery == null) throw new ArgumentNullException("query", "odataQuery");

            string referenceByKeys = GetODataReferenceByKeys(odataQuery.Keys);

            // ReferredResource means Get Association
            if (odataQuery.ReferredResource != null)
            {
                var rewriteResult = BuildBaseUri(this.ClassName, this.ConnectionUri, odataQuery.Keys, odataQuery.ReferredResource);
                Uri uri = BuildODataUri(rewriteResult.Item1 + referenceByKeys + "/" + rewriteResult.Item2, new Dictionary<string, string>() { { "$format", DefaultResponseFormat } });

                GetCmdlet(this.Cmdlet, uri, false, odataQuery);
            }
            else
            {
                Uri uri = new Uri(GetCustomUriHelper(this.ClassName, this.ConnectionUri, odataQuery.Keys) + referenceByKeys);
                uri = AppendFormatOption(uri);

                GetCmdlet(this.Cmdlet, uri, IsSingleInstance(referenceByKeys), odataQuery);
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
        protected virtual Tuple<string, string> BuildBaseUri(string originalUri, Uri connectionUri, List<Tuple<string, object>> referenceByKeys, string referredResource)
        {
            return RewriteBaseUri(GetCustomUriHelper(originalUri, connectionUri, referenceByKeys), referredResource);
        }

        /// <summary>
        /// Adds additional query parameters to OData uri
        /// </summary>
        /// <param name="uri">Base uri</param>
        /// <param name="queryParameters">Query parameters</param>
        /// <returns>Full uri</returns>
        [SuppressMessage("Microsoft.Design", "CA1054:UriParametersShouldNotBeStrings", Justification = "By Design")]
        protected virtual Uri BuildODataUri(string uri, Dictionary<string, string> queryParameters)
        {
            Debug.Assert((uri != null), "Server side endpoint Uri is pointing to NULL in ODataCmdletAdapter.BuildODataUri");

            StringBuilder sb = new StringBuilder(uri);

            string delimiter = "?";

            foreach (var queryParameter in queryParameters)
            {
                sb.Append(delimiter);
                sb.Append(queryParameter.Key + "=" + queryParameter.Value);
            }

            return (new Uri(sb.ToString()));
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
        protected virtual void ProcessActionRecord(Dictionary<string, object> filteredParameters, bool isForceParameterSpecified, List<Tuple<string, object>> keys, string referenceByKeys, string[] verbSplit)
        {
            Debug.Assert((filteredParameters != null), "filteredParameters is pointing to NULL in ODataCmdletAdapter.ProcessActionRecored");
            Debug.Assert((keys != null), "keys is pointing to NULL in ODataCmdletAdapter.ProcessActionRecored");
            Debug.Assert((referenceByKeys != null), "referenceByKeys is pointing to NULL in ODataCmdletAdapter.ProcessActionRecored");
            Debug.Assert((verbSplit != null), "verbSplit is pointing to NULL in ODataCmdletAdapter.ProcessActionRecored");
            Debug.Assert((verbSplit.Length >= 2), "verbSplit array has wrong number of items ODataCmdletAdapter.ProcessActionRecored");

            Uri uri = BuildODataUri(GetCustomUriHelper(this.ClassName, this.ConnectionUri, keys) + referenceByKeys + "/" + verbSplit[1], new Dictionary<string, string>() { { "$format", DefaultResponseFormat } });

            if (ShouldProcessHelper(this.Cmdlet, uri, true, isForceParameterSpecified))
            {
                var otherParameters = GetNonKeys(filteredParameters);
                GetActionCmdlet(this.Cmdlet, uri, otherParameters);
            }
        }

        /// <summary>
        /// This is implementation of Association cmdlet
        /// </summary>
        /// <param name="filteredParameters"></param>
        /// <param name="isForceParameterSpecified"></param>
        /// <param name="keys"></param>
        /// <param name="referenceByKeys"></param>
        /// <param name="verbSplit"></param>
        /// <param name="methodName"></param>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        protected virtual void ProcessAssociationRecord(Dictionary<string, object> filteredParameters, bool isForceParameterSpecified, List<Tuple<string, object>> keys, string referenceByKeys, string[] verbSplit, string methodName)
        {
            Debug.Assert((filteredParameters != null), "filteredParameters is pointing to NULL in ODataCmdletAdapter.ProcessAssociationRecord");
            Debug.Assert((keys != null), "keys is pointing to NULL in ODataCmdletAdapter.ProcessAssociationRecord");
            Debug.Assert((referenceByKeys != null), "referenceByKeys is pointing to NULL in ODataCmdletAdapter.ProcessAssociationRecord");
            Debug.Assert((verbSplit != null), "verbSplit is pointing to NULL in ODataCmdletAdapter.ProcessAssociationRecord");
            Debug.Assert((verbSplit.Length >= 2), "verbSplit array has wrong number of items ODataCmdletAdapter.ProcessAssociationRecord");
            Debug.Assert((methodName != null), "methodName is pointing to NULL in ODataCmdletAdapter.ProcessAssociationRecord");

            // For associations, we have to rewrite the base resource uri
            var rewriteResult = RewriteBaseUri(GetCustomUriHelper(this.ClassName, this.ConnectionUri, keys), verbSplit[1]);

            var referredResourceKeys = GetNonKeys(filteredParameters);
            string referredResourceReferenceByKeys = GetODataReferenceByKeys(referredResourceKeys);

            Uri uri = new Uri(rewriteResult.Item1 + referenceByKeys);

            if (ShouldProcessHelper(this.Cmdlet, uri, false, isForceParameterSpecified))
            {
                switch (verbSplit[1])
                {
                    case "Create":
                        CreateAssociationCmdlet(this.Cmdlet, uri, rewriteResult.Item2, referredResourceReferenceByKeys, GetCustomUriHelper(this.ClassName, this.ConnectionUri, keys));
                        break;
                    case "Delete":
                        DeleteAssociationCmdlet(this.Cmdlet, uri, rewriteResult.Item2, referredResourceReferenceByKeys);
                        break;
                    default:
                        throw new ArgumentException(String.Format(CultureInfo.InvariantCulture, Resources.IncorrectMethodName, methodName));
                }
            }
        }

        /// <summary>
        /// This is implementation of all other cmdlets: CUD, New/Remove associations, actions
        /// </summary>
        /// <param name="methodInvocationInfo"></param>
        public override void ProcessRecord(MethodInvocationInfo methodInvocationInfo)
        {
            if (methodInvocationInfo == null) throw new ArgumentNullException("methodInvocationInfo");

            Dictionary<string, object> filteredParameters = FilterClientSpecificMethodInvocationParameters(methodInvocationInfo.Parameters);
            bool isForceParameterSpecified = methodInvocationInfo.Parameters.Contains("Force") && methodInvocationInfo.Parameters["Force"].IsValuePresent;
            var keys = GetKeys(filteredParameters);
            string referenceByKeys = GetODataReferenceByKeys(keys);
            var verbSplit = methodInvocationInfo.MethodName.Split(':');
            Uri uri;

            // if methodName has a colon inside, it's an association or action
            // associations have format Association:Create/Delete:EntityName
            // actions have format Action:ActionName:(EntityName) - empty for service level actions
            if (verbSplit.Length > 1)
            {
                if (verbSplit.Length != 3)
                {
                    throw new ArgumentException(String.Format(CultureInfo.InvariantCulture, Resources.IncorrectMethodName, methodInvocationInfo.MethodName));
                }

                switch (verbSplit[0])
                {
                    case "Action":
                        ProcessActionRecord(filteredParameters, isForceParameterSpecified, keys, referenceByKeys, verbSplit);
                        break;
                    case "Association":
                        ProcessAssociationRecord(filteredParameters, isForceParameterSpecified, keys, referenceByKeys, verbSplit, methodInvocationInfo.MethodName);
                        break;
                    default:
                        throw new ArgumentException(String.Format(CultureInfo.InvariantCulture, Resources.IncorrectMethodName, methodInvocationInfo.MethodName));
                }
            }
            else
            {
                if (methodInvocationInfo.MethodName.Equals("Create"))
                {
                    uri = new Uri(GetCustomUriHelper(this.ClassName, this.ConnectionUri, keys));
                }
                else
                {
                    uri = BuildODataUri(GetCustomUriHelper(this.ClassName, this.ConnectionUri, keys) + referenceByKeys, new Dictionary<string, string>() { { "$format", DefaultResponseFormat } });
                }

                if (ShouldProcessHelper(this.Cmdlet, uri, false, isForceParameterSpecified))
                {
                    System.Management.Automation.PowerShell ps;
                    switch (methodInvocationInfo.MethodName)
                    {
                        case "Create":
                            ps = CreateCmdlet(this.Cmdlet, uri, keys, filteredParameters);
                            break;
                        case "Update":
                            string body = SerializeParameters(this.Cmdlet, null, ConvertFromKeyedCollection(filteredParameters), true);
                            ps = UpdateCmdlet(this.Cmdlet, uri, keys, filteredParameters, body);
                            break;
                        case "Delete":
                            ps = DeleteCmdlet(this.Cmdlet, uri, filteredParameters);
                            break;
                        default:
                            throw new NotImplementedException();
                    }
                    InvokePSPassStreamsToPSCmdlet(ps, this.Cmdlet, uri);
                    if (ps != null) ps.Dispose();
                }
            }
        }

        /// <summary>
        /// Rewrites uri, replacing last entity with a provided name, extracting the old entityName
        /// </summary>
        /// <param name="uri">uri to rewrite</param>
        /// <param name="newEntityName">name of the new entity</param>
        /// <returns>a pair: (new uri, old entity's name)</returns>
        [SuppressMessage("Microsoft.Design", "CA1054:UriParametersShouldNotBeStrings", MessageId = "0#")]
        protected Tuple<string, string> RewriteBaseUri(string uri, string newEntityName)
        {
            Debug.Assert((uri != null), "Server side endpoint Uri is pointing to NULL in ODataCmdletAdapter.RewriteBaseUri");

            int index = uri.LastIndexOf("/", StringComparison.OrdinalIgnoreCase);

            if (index <= 0)
            {
                throw new ArgumentException(String.Format(CultureInfo.InvariantCulture, Resources.IncorrectResourceUri, uri));
            }

            string newResourceUri = uri.Substring(0, index + 1) + newEntityName;
            string referredResourceName = uri.Substring(index + 1, uri.Length - index - 1);

            return new Tuple<string, string>(newResourceUri, referredResourceName);
        }

        /// <summary>
        /// Gets a list of key parameters out of all parameters
        /// </summary>
        /// <param name="parameters">all parameters</param>
        /// <returns>keys</returns>
        private List<Tuple<string, object>> GetKeys(Dictionary<string, object> parameters)
        {
            var keys = new List<Tuple<string, object>>();

            foreach (var parameter in parameters)
            {
                var parameterSplit = parameter.Key.Split(':');
                if (parameterSplit.Length > 1)
                {
                    if (parameterSplit[1] == "Key")
                    {
                        keys.Add(new Tuple<string, object>(parameterSplit[0], parameter.Value));
                    }
                }
            }

            return keys;
        }

        /// <summary>
        /// Gets a list of non-key parameters out of all parameters
        /// </summary>
        /// <param name="parameters">all parameters</param>
        /// <returns>keys</returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        protected List<Tuple<string, object>> GetNonKeys(Dictionary<string, object> parameters)
        {
            var nonKeys = new List<Tuple<string, object>>();

            foreach (var parameter in parameters)
            {
                var parameterSplit = parameter.Key.Split(':');
                if (parameterSplit.Length == 1)
                {
                    nonKeys.Add(new Tuple<string, object>(parameterSplit[0], parameter.Value));
                }
            }

            return nonKeys;
        }

        /// <summary>
        /// This is an implementation of Get Operation.
        /// </summary>
        /// <param name="cmdlet">
        /// Cmdlet Object to pass data to PowerShell.
        /// </param>
        /// <param name="uri">
        /// Full Resource Uri.
        /// </param>
        /// <param name="isSingleInstance">
        /// Tell if the result is a single resource instance.
        /// </param>
        /// <param name="oDataQueryBuilder">
        /// Instance of ODATA specific Query Builder.
        /// </param>
        protected void GetCmdlet(PSCmdlet cmdlet, Uri uri, bool isSingleInstance, ODataQueryBuilder oDataQueryBuilder)
        {
            Debug.Assert((uri != null), "Uri is pointing to NULL in ODataCmdletAdapter.GetCmdlet");

            using (var ps = System.Management.Automation.PowerShell.Create())
            {
                uri = TryUpdateQueryOptions(uri, oDataQueryBuilder);

                if (isSingleInstance)
                {
                    ps.AddCommand(Command).AddParameter("Verbose").AddParameter("Debug");
                }
                else
                {
                    ps.AddCommand(Command).AddParameter("Verbose").AddParameter("Debug");
                    ProcessAdditionalInfo(ps);
                }

                InvokePSPassStreamsToPSCmdlet(ps, cmdlet, uri);
            }
        }

        /// <summary>
        /// TryUpdateQueryOptions is a helper function used to update the query options (i.e., Filters, Top, Skip, OrderBy) to the base Uri.
        /// </summary>
        /// <param name="uri">Base Uri.</param>
        /// <param name="adapterQueryBuilder">Adapter specific Query Builder</param>
        /// <returns>If Query options is specified, Base Uri would be appended with the Query options or else the Base Uri would be returned.</returns>
        protected virtual Uri TryUpdateQueryOptions(Uri uri, ODataQueryBuilder adapterQueryBuilder)
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

                    return (new Uri(uri.AbsoluteUri + adapterQueryBuilder.ConcatinationOperator + queryOperation.ToString()));
                }
            }

            return uri;
        }

        /// <summary>
        /// This is a helper method used to support -WhatIf and -Confirm scenarios.
        /// </summary>
        /// <param name="cmdlet">
        /// PSCmdlet.</param>
        /// <param name="uri">
        /// Target Uri.
        /// </param>
        /// <param name="isActionCommand">
        /// Indicates if cmdlet under execution is targeting Action functionality.
        /// </param>
        /// <param name="isForceParameterSpecified">
        /// Indicates if cliet side -Force Parameter is specified.
        /// </param>
        /// <returns>True is Processing is required.</returns>
        protected bool ShouldProcessHelper(PSCmdlet cmdlet, Uri uri, bool isActionCommand, bool isForceParameterSpecified)
        {
            Debug.Assert(((uri != null) && (uri.AbsoluteUri != null)), "Uri is pointing to NULL in ODataCmdletAdapter.ShouldProcessHelper");

            string shouldProcessMessage = null;
            string shouldContinueMessage = null;

            if (isActionCommand)
            {
                PrivateDateValidationHelper(cmdlet, "Namespace");
                shouldProcessMessage = String.Format(CultureInfo.InvariantCulture, Resources.ShouldProcessActionMessage, PrivateData["Namespace"], uri.AbsoluteUri);
                shouldContinueMessage = String.Format(CultureInfo.InvariantCulture, Resources.ShouldContinueActionMessage, cmdlet.MyInvocation.MyCommand, PrivateData["Namespace"], uri.AbsoluteUri);
            }
            else
            {
                PrivateDateValidationHelper(cmdlet, "EntityTypeName");
                PrivateDateValidationHelper(cmdlet, "EntitySetName");
                shouldProcessMessage = String.Format(CultureInfo.InvariantCulture, ShouldProcessCrudMessage, PrivateData["EntityTypeName"], PrivateData["EntitySetName"], uri.AbsoluteUri);
                shouldContinueMessage = String.Format(CultureInfo.InvariantCulture, ShouldContinueCrudMessage, cmdlet.MyInvocation.MyCommand, PrivateData["EntityTypeName"], PrivateData["EntitySetName"], uri.AbsoluteUri);
            }

            if (cmdlet.ShouldProcess(shouldProcessMessage))
            {
                if (isForceParameterSpecified || cmdlet.ShouldContinue(shouldContinueMessage, ""))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// PrivateDateValidationHelper is a helper method used to validate that the required private data from the CDXML is avaliable for processing.
        /// </summary>
        /// <param name="cmdlet">
        /// PS Cmdlet under execution.
        /// </param>
        /// <param name="key">
        /// Private Data Key.
        /// </param>
        protected void PrivateDateValidationHelper(PSCmdlet cmdlet, string key)
        {
            Debug.Assert((cmdlet != null), "Cmdlet Instance is pointing to NULL");
            Debug.Assert((key != null), "PrivateData Key is pointing to NULL");

            if (!PrivateData.ContainsKey(key))
            {
                string message = String.Format(CultureInfo.InvariantCulture, Resources.MissingCmdletAdapterPrivateData, cmdlet.MyInvocation.MyCommand.Module.Path, key);
                InvalidOperationException exception = new InvalidOperationException(message);
                ErrorRecord errorRecord = new ErrorRecord(exception, "ODataEndpointProxyMissingPrivateData", ErrorCategory.InvalidData, key);
                cmdlet.ThrowTerminatingError(errorRecord);
            }
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
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        protected virtual System.Management.Automation.PowerShell CreateCmdlet(PSCmdlet cmdlet, Uri uri, List<Tuple<string, object>> keys, Dictionary<string, object> parameters)
        {
            string body = SerializeParameters(cmdlet, keys, ConvertFromKeyedCollection(parameters), true);
            var ps = System.Management.Automation.PowerShell.Create();
            string action;
            if (PrivateData.TryGetValue("CreateRequestMethod", out action) == false) Debug.Assert(false, "CreateRequestMethod not present in PrivateData");
            action = action.ToUpperInvariant();
            ps.AddCommand(Command).AddParameter("Method", action).AddParameter("Body", body).AddParameter("Verbose").AddParameter("Debug");
            SetContentType(ps);
            return ps;
        }

        /// <summary>
        /// This is an implementation of Update Operation.
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
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists")]
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        protected virtual System.Management.Automation.PowerShell UpdateCmdlet(PSCmdlet cmdlet, Uri uri, List<Tuple<string, object>> keys, Dictionary<string, object> parameters, string body)
        {
            var ps = System.Management.Automation.PowerShell.Create();
            string action;
            if (PrivateData.TryGetValue("UpdateRequestMethod", out action) == false) Debug.Assert(false, "UpdateRequestMethod not present in PrivateData");
            action = action.ToUpperInvariant();
            ps.AddCommand(Command).AddParameter("Method", action).AddParameter("Body", body).AddParameter("Verbose").AddParameter("Debug");
            SetContentType(ps);
            return ps;
        }

        /// <summary>
        /// This is implementation of the Delete operation
        /// </summary>
        /// <param name="cmdlet">Cmdlet object to pass data to PowerShell</param>
        /// <param name="uri">Full resource uri</param>
        /// <param name="parameters">Parameters passed in cmdlet</param>
        protected virtual System.Management.Automation.PowerShell DeleteCmdlet(PSCmdlet cmdlet, Uri uri, Dictionary<string, object> parameters)
        {
            var ps = System.Management.Automation.PowerShell.Create();
            ps.AddCommand(Command).AddParameter("Method", "DELETE").AddParameter("Verbose").AddParameter("Debug");
            return ps;
        }

        /// <summary>
        /// This is an implementation of Add Association Operation.
        /// </summary>
        /// <param name="cmdlet">
        /// Cmdlet Object to pass data to PowerShell.
        /// </param>
        /// <param name="uri">
        /// Full Resource Uri.
        /// </param>
        /// <param name="referredResourceName">
        /// Associated Resource Name.
        /// </param>
        /// <param name="referredResourceReferenceByKeys">
        /// Associated Resource Key.
        /// </param>
        /// <param name="referredUri">
        /// Associated Resource Key.
        /// </param>
        [SuppressMessage("Microsoft.Design", "CA1054:UriParametersShouldNotBeStrings", MessageId = "4#")]
        [SuppressMessage("Microsoft.Usage", "CA2234:PassSystemUriObjectsInsteadOfStrings")]
        protected void CreateAssociationCmdlet(PSCmdlet cmdlet, Uri uri, string referredResourceName, string referredResourceReferenceByKeys, string referredUri)
        {
            Debug.Assert(((uri != null)), "Uri is pointing to NULL in ODataCmdletAdapter.CreateAssociationCmdlet");

            // odata.entityRef for OData v4
            // uri for Json Verbose
            // url for Json Light
            string body = "{\"url\":\"" + referredUri + referredResourceReferenceByKeys + "\"}";
            uri = new Uri(uri, "/$links/" + referredResourceName);

            using (var ps = System.Management.Automation.PowerShell.Create())
            {
                ps.AddCommand(Command).AddParameter("Method", "POST").AddParameter("Body", body).AddParameter("Verbose").AddParameter("Debug");
                SetContentType(ps);
                InvokePSPassStreamsToPSCmdlet(ps, cmdlet, uri);
            }
        }

        /// <summary>
        /// This is an implementation of Delete Association Operation.
        /// </summary>
        /// <param name="cmdlet">
        /// Cmdlet Object to pass data to PowerShell.
        /// </param>
        /// <param name="uri">
        /// Full Resource Uri.
        /// </param>
        /// <param name="referredResourceName">
        /// Associated Resource Name.
        /// </param>
        /// <param name="referredResourceReferenceByKeys">
        /// Associated Resource Key.
        /// </param>
        [SuppressMessage("Microsoft.Usage", "CA2234:PassSystemUriObjectsInsteadOfStrings")]
        protected void DeleteAssociationCmdlet(PSCmdlet cmdlet, Uri uri, string referredResourceName, string referredResourceReferenceByKeys)
        {
            Debug.Assert(((uri != null)), "Uri is pointing to NULL in ODataCmdletAdapter.DeleteAssociationCmdlet");

            uri = new Uri(uri, "/$links/" + referredResourceName + referredResourceReferenceByKeys);
            using (var ps = System.Management.Automation.PowerShell.Create())
            {
                ps.AddCommand(Command).AddParameter("Method", "DELETE").AddParameter("Verbose").AddParameter("Debug"); ;

                InvokePSPassStreamsToPSCmdlet(ps, cmdlet, uri);
            }
        }

        /// <summary>
        /// Executed Action
        /// </summary>
        /// <param name="cmdlet">Cmdlet object to return to</param>
        /// <param name="uri">Uri to the resource+action</param>
        /// <param name="otherParameters">HTTP Body parameters</param>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        protected void GetActionCmdlet(PSCmdlet cmdlet, Uri uri, List<Tuple<string, object>> otherParameters)
        {
            string body = SerializeParameters(cmdlet, null, otherParameters, false);
            using (var ps = System.Management.Automation.PowerShell.Create())
            {
                string action;
                if (PrivateData.TryGetValue("CreateRequestMethod", out action) == false) Debug.Assert(false, "CreateRequestMethod not present in PrivateData");
                action = action.ToUpperInvariant();
                ps.AddCommand(Command).AddParameter("Method", action).AddParameter("Body", body).AddParameter("Verbose").AddParameter("Debug");
                SetContentType(ps);
                InvokePSPassStreamsToPSCmdlet(ps, cmdlet, uri);
            }
        }

        /// <summary>
        /// Converts lists of parameters from KeyedCollection used by cmdletization
        /// </summary>
        /// <param name="parameters">input collection</param>
        /// <returns>list of parameters</returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists")]
        protected virtual List<Tuple<string, object>> ConvertFromKeyedCollection(Dictionary<string, object> parameters)
        {
            var result = new List<Tuple<string, object>>();

            foreach (var parameter in parameters)
            {
                result.Add(new Tuple<string, object>(parameter.Key, parameter.Value));
            }

            return result;
        }

        /// <summary>
        /// FilterClientSpecificMethodInvocationParameters is a helper function used to filter the client 
        /// specific paramters so that they are not sent over the wire.
        /// </summary>
        /// <param name="parameters">
        /// Method Invokation parameters</param>
        /// <returns>Filtered list of method invokation parameters (with out client specific parameters).</returns>
        private Dictionary<string, object> FilterClientSpecificMethodInvocationParameters(KeyedCollection<string, MethodParameter> parameters)
        {
            Dictionary<string, object> filteredParameters = new Dictionary<string, object>();
            foreach (var parameter in parameters)
            {
                // -Force is the only client specific method Invokation parameter that we are currently supporting.
                if (!parameter.Name.ToString().Equals("Force"))
                {
                    filteredParameters.Add(parameter.Name, parameter.Value);
                }
            }

            return filteredParameters;
        }

        /// <summary>
        /// ProcessCommonParameters is a helper method used to append 
        /// CertificateThumbprint and Credential Parameters is they are specified.
        /// It also validates and notifies the user, if the client side proxy is trying to establish an un 
        /// secured connection with the server side endpoint.
        /// </summary>
        /// <param name="powerShell">PowerShell Object to be invoked on.</param>
        /// <param name="cmdlet">Client side proxy PSCmdlet used interacting with server side endpoint.</param>
        /// <param name="uri">Uri of the server side End point.</param>
        protected virtual void ProcessCommonParameters(System.Management.Automation.PowerShell powerShell, PSCmdlet cmdlet, Uri uri)
        {
            if (powerShell != null && powerShell.Commands != null && powerShell.Commands.Commands != null)
            {
                foreach (Command currentCommand in powerShell.Commands.Commands)
                {
                    if (string.Equals(currentCommand.CommandText, Command, StringComparison.OrdinalIgnoreCase))
                    {
                        if (null != this.Credential)
                        {
                            currentCommand.Parameters.Add("Credential", this.Credential);
                        }

                        if (null != this.Headers)
                        {
                            currentCommand.Parameters.Add("Headers", this.Headers);
                        }

                        if (null != this.CertificateThumbprint)
                        {
                            currentCommand.Parameters.Add("CertificateThumbprint", this.CertificateThumbprint);
                        }

                        Uri endPointUri = uri;
                        if (string.Equals(endPointUri.Scheme, "http", StringComparison.OrdinalIgnoreCase) && !this.AllowUnsecureConnection.IsPresent)
                        {
                            string message = String.Format(CultureInfo.InvariantCulture, AllowUnsecureConnectionErrorString, cmdlet.MyInvocation.MyCommand.Name, uri.AbsoluteUri, "ConnectionUri");
                            InvalidOperationException exception = new InvalidOperationException(message);
                            ErrorRecord errorRecord = new ErrorRecord(exception, "ODataEndpointProxyUnSecureConnection", ErrorCategory.InvalidData, uri.AbsoluteUri);
                            cmdlet.ThrowTerminatingError(errorRecord);
                        }

                        currentCommand.Parameters.Add("Uri", uri);

#if CORECLR
                        if (this.SkipCertificateCheck.IsPresent)
                        {
                            currentCommand.Parameters.Add("SkipCertificateCheck", true);
                        }
#endif
                    }
                }
            }
        }

        /// <summary>
        /// Calls Invoke() on PowerShell object and passes it's output, errors, warnings, verbose and debug to target PSCmdlet
        /// </summary>
        /// <param name="ps">PowerShell object to be invoked on.</param>
        /// <param name="target">target cmdlet to pass output to.</param>
        /// <param name="uri">Uri of the server side End point.</param>
        protected void InvokePSPassStreamsToPSCmdlet(System.Management.Automation.PowerShell ps, PSCmdlet target, Uri uri)
        {
            Debug.Assert((ps != null), "PowerShell instance is pointing to NULL");
            Debug.Assert((target != null), "Current cmdlet instance is pointing to NULL");
            Debug.Assert(((uri != null) && (uri.AbsoluteUri != null)), "Uri is pointing to NULL in ODataCmdletAdapter.InvokePSPassStreamsToPSCmdlet");

            // EntityTypeName does not have to be validated for Service-level actions 
            if (String.IsNullOrEmpty(this.ActionEntityType))
            {
                PrivateDateValidationHelper(target, "EntityTypeName");
            }

            this.ProcessCommonParameters(ps, target, uri);

            try
            {
                RegisterToPowerShellStreams(ps);

                IAsyncResult async = ps.BeginInvoke<int, PSObject>(null, output);
                ps.EndInvoke(async);

                ProcessStreamRecords(target, uri);
            }
            catch (Exception ex)
            {
                if (this.PassInnerException)
                {
                    throw;
                }
                else
                {
                    string message = String.Format(CultureInfo.InvariantCulture, Resources.InvokeRestMethodTerminatingError, target.MyInvocation.MyCommand.Module.Path, uri.AbsoluteUri);
                    InvalidOperationException exception = new InvalidOperationException(message, ex);
                    ErrorRecord errorRecord = new ErrorRecord(exception, "ODataEndpointProxyInvokeFailure", ErrorCategory.InvalidOperation, uri.AbsoluteUri);
                    target.ThrowTerminatingError(errorRecord);
                }
            }
            finally
            {
                UnRegisterFromPowerShellStreams(ps);
            }
        }

        /// <summary>
        /// ProcessStreamRecords is a helper function used to channel the different stream records to the client side.
        /// </summary>
        /// <param name="targetCmdlet">Current executing PSCmdlet.</param>
        /// <param name="uri">Uri of the server side End point.</param>
        private void ProcessStreamRecords(PSCmdlet targetCmdlet, Uri uri)
        {
            Debug.Assert((targetCmdlet != null), "Current cmdlet instance is pointing to NULL");
            Debug.Assert((uri != null), "Server side endpoint Uri is pointing to NULL in ODataCmdletAdapter.ProcessStreamRecords");

            Object endPointRecord = null;

            while (endPointRecordQueue.TryDequeue(out endPointRecord))
            {
                PSObject currentOutputObject = endPointRecord as PSObject;
                if (currentOutputObject != null)
                {
                    ConvertToOutputObject(targetCmdlet, uri, currentOutputObject);
                    continue;
                }

                ErrorRecord currentErrorRecord = endPointRecord as ErrorRecord;
                if (currentErrorRecord != null)
                {
                    targetCmdlet.WriteError(currentErrorRecord);
                    continue;
                }

                VerboseRecord currentVerboseRecord = endPointRecord as VerboseRecord;
                if (currentVerboseRecord != null)
                {
                    targetCmdlet.WriteVerbose(currentVerboseRecord.Message);
                    continue;
                }

                InformationRecord currentInformationRecord = endPointRecord as InformationRecord;
                if (currentInformationRecord != null)
                {
                    targetCmdlet.WriteInformation(currentInformationRecord);
                    continue;
                }

                DebugRecord currentDebugRecord = endPointRecord as DebugRecord;
                if (currentDebugRecord != null)
                {
                    targetCmdlet.WriteDebug(currentDebugRecord.Message);
                    continue;
                }

                WarningRecord currentWarningRecord = endPointRecord as WarningRecord;
                if (currentWarningRecord != null)
                {
                    targetCmdlet.WriteWarning(currentWarningRecord.Message);
                }
            }
        }

        /// <summary>
        /// Helper method that returns Type of returned object
        /// </summary>
        /// <returns></returns>
        protected virtual Type GetRequestedType(PSCmdlet cmdlet)
        {
            PrivateDateValidationHelper(cmdlet, "EntityTypeName");
            return System.Management.Automation.LanguagePrimitives.ConvertTo<Type>(PrivateData["EntityTypeName"]);
        }

        /// <summary>
        /// Write the output object after type casting if possible
        /// </summary>
        /// <param name="targetCmdlet">Current executing PSCmdlet.</param>
        /// <param name="uri">Uri of the server side End point.</param>
        /// <param name="currentOutputObject">The output object.</param>
        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        protected virtual void ConvertToOutputObject(PSCmdlet targetCmdlet, Uri uri, PSObject currentOutputObject)
        {
            if (currentOutputObject == null) return;
            Debug.Assert(((uri != null) && (uri.AbsoluteUri != null)), "Uri is pointing to NULL in ODataCmdletAdapter.ConvertToOutputObject");

            bool successfullyTypeCasted = false;
            if (!this.AllowAdditionalData.IsPresent)
            {
                try
                {
                    Type requestedType = GetRequestedType(targetCmdlet);
                    var currentTypeCastedObject = System.Management.Automation.LanguagePrimitives.ConvertPSObjectToType(currentOutputObject, requestedType, true, CultureInfo.InvariantCulture, true);

                    if (currentTypeCastedObject != null)
                    {
                        var odataIdProperty = currentOutputObject.Properties["@odata.id"];

                        if (odataIdProperty != null)
                        {
                            Type currentTypeCastedObjectType = currentTypeCastedObject.GetType();
#if CORECLR
                            System.Reflection.FieldInfo odataIdPropertyToUpdate = System.Reflection.TypeExtensions.GetField(currentTypeCastedObjectType,"odataId");
#else
                            System.Reflection.FieldInfo odataIdPropertyToUpdate = currentTypeCastedObjectType.GetField("odataId");
#endif
                            if (odataIdPropertyToUpdate != null)
                            {
                                try
                                {
                                    // Set odataId property using @odata.id as a source
                                    odataIdPropertyToUpdate.SetValue(currentTypeCastedObject, odataIdProperty.ToString().Replace("string @odata.id=", String.Empty));
                                }
                                catch
                                {
                                    // Do nothing. The object should be returned even if odataId property can't be updated.
                                }
                            }
                        }

                        successfullyTypeCasted = true;
                        targetCmdlet.WriteObject(currentTypeCastedObject);
                    }
                }
                catch (Exception ex)
                {
                    string message = String.Format(CultureInfo.InvariantCulture, AllowAdditionalDataErrorString, PrivateData["EntityTypeName"], PrivateData["EntityTypeName"]);
                    InvalidOperationException exception = new InvalidOperationException(message, ex);
                    ErrorRecord errorRecord = new ErrorRecord(exception, "ODataEndpointProxyAdditionalData", ErrorCategory.InvalidArgument, uri.AbsoluteUri);
                    targetCmdlet.ThrowTerminatingError(errorRecord);
                }
            }

            if (!successfullyTypeCasted)
            {
                if (!this.AllowAdditionalData.IsPresent)
                {
                    string warningMessage = String.Format(CultureInfo.InvariantCulture, Resources.TypeConversionFailureWarningMessage, PrivateData["EntityTypeName"]);
                    targetCmdlet.WriteWarning(warningMessage);
                }
                // write the object sent from the server as is(with out type casting).
                targetCmdlet.WriteObject(currentOutputObject);
            }
        }

        /// <summary>
        /// RegisterToPowerShellStreams is a helper method used to register handlers to all the stream 
        /// records being generated during command invocation.
        /// </summary>
        /// <param name="powerShell">PowerShell object to be invoked on.</param>
        private void RegisterToPowerShellStreams(System.Management.Automation.PowerShell powerShell)
        {
            Debug.Assert((powerShell != null), "PowerShell instance is pointing to NULL");

            output.DataAdded += HandleDataAdded;

            powerShell.Streams.Verbose.DataAdded += HandleRecordsAdded<VerboseRecord>;
            powerShell.Streams.Information.DataAdded += HandleRecordsAdded<InformationRecord>;
            powerShell.Streams.Debug.DataAdded += HandleRecordsAdded<DebugRecord>;
            powerShell.Streams.Warning.DataAdded += HandleRecordsAdded<WarningRecord>;
            powerShell.Streams.Error.DataAdded += HandleNonTerminatingErrorAdded;
        }

        /// <summary>
        /// UnRegisterFromPowerShellStreams is a helper method used to unregister handlers to all the stream 
        /// records being generated during command invocation.
        /// </summary>
        /// <param name="powerShell">PowerShell object to be invoked on.</param>
        private void UnRegisterFromPowerShellStreams(System.Management.Automation.PowerShell powerShell)
        {
            Debug.Assert((powerShell != null), "PowerShell instance is pointing to NULL");

            output.DataAdded -= HandleDataAdded;
            output.Clear();

            powerShell.Streams.Verbose.DataAdded -= HandleRecordsAdded<VerboseRecord>;
            powerShell.Streams.Information.DataAdded -= HandleRecordsAdded<InformationRecord>;
            powerShell.Streams.Debug.DataAdded -= HandleRecordsAdded<DebugRecord>;
            powerShell.Streams.Warning.DataAdded -= HandleRecordsAdded<WarningRecord>;
            powerShell.Streams.Error.DataAdded -= HandleNonTerminatingErrorAdded;
        }

        /// <summary>
        /// HandleRecordsAdded is a handler for capturing all stream records 
        /// (Verbose, Debug, Warning, Non-Terminating Error) being generated by the server side endpoint.
        /// </summary>
        /// <typeparam name="T">Type of Stream record.</typeparam>
        /// <param name="sender">Sender</param>
        /// <param name="eventArgs">Event Args</param>
        private void HandleRecordsAdded<T>(object sender, DataAddedEventArgs eventArgs)
        {
            var serverSideRecords = sender as PSDataCollection<T>;
            if (serverSideRecords != null && serverSideRecords.Count > eventArgs.Index)
            {
                endPointRecordQueue.Enqueue(serverSideRecords[eventArgs.Index]);
            }
        }

        /// <summary>
        /// HandleNonTerminatingErrorAdded is a handler for capturing non-terminating error records being generated
        /// during powershell command invocation.
        /// </summary>
        /// <param name="sender">Sender</param>
        /// <param name="eventArgs">Event Args</param>
        private void HandleNonTerminatingErrorAdded(object sender, DataAddedEventArgs eventArgs)
        {
            var errorRecords = sender as PSDataCollection<ErrorRecord>;
            if (errorRecords != null && errorRecords.Count > eventArgs.Index)
            {
                endPointRecordQueue.Enqueue(errorRecords[eventArgs.Index]);
            }
        }

        /// <summary>
        /// HandleDataAdded is a handler for capturing objects written to the pipeline ny the serverside endpoint.
        /// </summary>
        /// <param name="sender">Sender</param>
        /// <param name="eventArgs">EventArgs</param>
        private void HandleDataAdded(object sender, DataAddedEventArgs eventArgs)
        {
            var dataRecords = sender as PSDataCollection<PSObject>;
            if (dataRecords != null && dataRecords.Count > eventArgs.Index)
            {
                endPointRecordQueue.Enqueue(dataRecords[eventArgs.Index]);
            }
        }

        /// <summary>
        /// Serializes parameters into OData-formatted json
        /// </summary>
        /// <param name="cmdlet">PS Cmdlet under execution.</param>
        /// <param name="keys">Keys to be serialized</param>
        /// <param name="parameters">Parameters that need to be serialized</param>
        /// <param name="includeTypeName">IncludeTypeName adds Entity Type Name for CRUD operations</param>
        /// <returns>Json string</returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists")]
        protected virtual string SerializeParameters(PSCmdlet cmdlet, List<Tuple<string, object>> keys, List<Tuple<string, object>> parameters, bool includeTypeName)
        {
            var returnObject = new Dictionary<string, object>();

            if (keys != null)
            {
                foreach (var key in keys)
                {
                    if (key.Item2 != null)
                    {
                        returnObject = AddParameterToDictionary(returnObject, key.Item1, key.Item2);
                    }
                }
            }

            if (parameters != null)
            {
                foreach (var parameter in parameters)
                {
                    returnObject = AddParameterToDictionary(returnObject, parameter.Item1, parameter.Item2);
                }
            }

            if (includeTypeName)
            {
                AddTypeName(cmdlet, returnObject);
            }

            //JavaScriptSerializer jss = new JavaScriptSerializer();
            //return jss.Serialize(returnObject);
            return string.Empty;
        }

        /// <summary>
        /// Adds a key/other parameter to an OData serialization dictionary
        /// </summary>
        /// <param name="parameterDictionary">Dictionary to add the parameters to</param>
        /// <param name="name">Parameter name</param>
        /// <param name="value">Parameter value</param>
        /// <returns></returns>
        protected virtual Dictionary<string, object> AddParameterToDictionary(Dictionary<string, object> parameterDictionary, string name, object value)
        {
            if (value != null)
            {
                var parameterType = value.GetType();

                if (parameterType.IsArray)
                {
                    parameterDictionary.Add(name, new Dictionary<string, object>() { { "results", value } });
                }
                else
                {
                    parameterDictionary.Add(name, value);
                }
            }

            return parameterDictionary;
        }

        /// <summary>
        /// Formats part of the URI, which specifies all key and value pairs
        /// </summary>
        /// <param name="referenceByKeys">part of the URI, which specifies all key and value pairs</param>
        /// <param name="format">Format of the key name/value pair in the uri</param>
        /// <returns>formated key/value pair string</returns>
        protected virtual string FormatODataReferenceByKeys(string referenceByKeys, UriResourcePathKeyFormat format)
        {
            return "(" + referenceByKeys + ")";
        }

        /// <summary>
        /// Gets OData resource reference by keys e.g. "(Id=7,Name='apple')" for ~/.svc/Product(Id=7,Name='apple').
        /// </summary>
        /// <param name="keys">Referred keys.</param>
        /// <returns>Reference by keys string for current resource.</returns>
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists")]
        public virtual string GetODataReferenceByKeys(List<Tuple<string, object>> keys)
        {
            if (keys == null) throw new ArgumentNullException("keys");
            string result = "";
            string delimiter = "";

            if (keys.Count == 0)
            {
                return "";
            }

            foreach (var key in keys)
            {
                if (key.Item2 != null)
                {
                    var type = key.Item2.GetType();

                    if (type == typeof(short) || type == typeof(int) || type == typeof(long))
                    {
                        result += delimiter + key.Item1 + "=" + key.Item2;
                    }
                    else if (type == typeof(Guid))
                    {
                        result += delimiter + key.Item1 + "=guid'" + key.Item2 + "'";
                    }
                    else
                    {
                        result += delimiter + key.Item1 + "='" + key.Item2 + "'";
                    }
                    delimiter = ", ";
                }
                else
                {
                    throw new ArgumentException(String.Format(CultureInfo.InvariantCulture, Resources.NullValueForKey, key.Item1));
                }
            }

            return FormatODataReferenceByKeys(result, UriResourcePathKeyFormat.EmbeddedKey);
        }

        /// <summary>
        /// GetCustomUriHelper is a helper method used to detect/construct the connection Uri to be used for
        /// the OData endpoint.
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
        [SuppressMessage("Microsoft.Design", "CA1006:DoNotNestGenericTypesInMemberSignatures")]
        [SuppressMessage("Microsoft.Design", "CA1055:UriReturnValuesShouldNotBeStrings")]
        [SuppressMessage("Microsoft.Design", "CA1054:UriParametersShouldNotBeStrings", MessageId = "0#")]
        [SuppressMessage("Microsoft.Design", "CA1002:DoNotExposeGenericLists")]
        public virtual string GetCustomUriHelper(string defaultUri, Uri connectionUri, List<Tuple<string, object>> keys)
        {
            if (defaultUri == null) throw new ArgumentNullException("defaultUri");

            // The defaultUri contains the baseUri plus the entity name.
            // It's a common scenario where the same OData service is avaliable on
            // multiple endpoints(for scale/redundancy etc). By using the ConnectionUri property, 
            // user would have an ability to use the same serice from multiple endpoints.
            // ConnectionUri just contains the Uri to the service. If its supplied to this helper function to
            // construct the fully qualified Uri to the entity on the endpoint (by appending the entity 
            // name to the supplied ConnectionUri).
            if (connectionUri == null)
            {
                return defaultUri;
            }

            if (string.IsNullOrEmpty(defaultUri))
            {
                throw new ArgumentException(String.Format(CultureInfo.InvariantCulture, Resources.IncorrectResourceUri, defaultUri));
            }

            int index = defaultUri.LastIndexOf("/", StringComparison.OrdinalIgnoreCase);

            if (index <= 0)
            {
                throw new ArgumentException(String.Format(CultureInfo.InvariantCulture, Resources.IncorrectResourceUri, defaultUri));
            }

            string entityName = defaultUri.Substring(index + 1);
            return String.Format(CultureInfo.InvariantCulture, "{0}/{1}", connectionUri.ToString().TrimEnd('/'), entityName);
        }

        /// <summary>
        /// Add typeName to dictionary
        /// </summary>
        /// <param name="cmdlet">The cmdlet being processed</param>
        /// <param name="returnObject">The dictionary</param>
        protected virtual void AddTypeName(PSCmdlet cmdlet, Dictionary<string, object> returnObject)
        {
            PrivateDateValidationHelper(cmdlet, "EntityTypeName");
            returnObject.Add("__metadata", new Dictionary<string, string>() { { "type", PrivateData["EntityTypeName"] } });
        }

        /// <summary>
        /// This method adds the Format option in url
        /// </summary>
        /// <param name="uri">url</param>
        /// <returns>url after adding format json</returns>
        protected virtual Uri AppendFormatOption(Uri uri)
        {
            Debug.Assert(((uri != null) && (uri.AbsoluteUri != null)), "Uri is pointing to NULL in ODataCmdletAdapter.AppendFormatOption");

            return (new Uri(uri.AbsoluteUri + "?$format=json"));
        }

        /// <summary>
        /// Returns if the return value is a single instance
        /// </summary>
        /// <param name="referencedByKeys">Keys string</param>
        /// <returns>True if result is a single instance. False otherwise</returns>
        protected virtual bool IsSingleInstance(string referencedByKeys)
        {
            return !String.IsNullOrEmpty(referencedByKeys);
        }

        /// <summary>
        /// ProcessAdditionalInfo is a helper method for supporting processing of additional information 
        /// sent from the server. The server side end point, might be interested in sending additional 
        /// details (such as number of records in the response object, Uri to be used to get batches of 
        /// response data if server driven paging is enabled, the metadata specific to response object etc). 
        /// The Metadata used for generating the client side proxy may not have this details and server 
        /// may choose to send it.
        /// </summary>
        /// <param name="powerShell">The Powershell command executed from the client side proxy.</param>
        protected virtual void ProcessAdditionalInfo(System.Management.Automation.PowerShell powerShell)
        {
            Debug.Assert((powerShell != null), "PowerShell instance is pointing to NULL in ODataCmdletAdapter.AddForEachObject");

            foreach (Command currentCommand in powerShell.Commands.Commands)
            {
                if (string.Equals(currentCommand.CommandText, Command, StringComparison.OrdinalIgnoreCase))
                {
                    currentCommand.Parameters.Add("OutVariable", "responseObjects");
                }
            }

            powerShell.AddScript(additionalInfoScript);
        }

        /// <summary>
        /// Set the contentType property for Invoke-RestMethod
        /// </summary>
        /// <param name="powerShell">The powershell command</param>
        protected virtual void SetContentType(System.Management.Automation.PowerShell powerShell)
        {
            Debug.Assert((powerShell != null), "PowerShell instance is pointing to NULL in ODataCmdletAdapter.SetContentType");

            powerShell.AddParameter("ContentType", "application/json;charset=utf-8");
        }
    }
}