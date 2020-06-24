Import-LocalizedData LocalizedData -FileName Microsoft.PowerShell.ODataUtilsStrings.psd1

# Add .NET classes used by the module
if ($PSEdition -eq "Core")
{
   Add-Type -TypeDefinition $script:BaseClassDefinitions -ReferencedAssemblies @([System.Collections.ArrayList].Assembly.Location,[System.Management.Automation.PSCredential].Assembly.Location)
}
else
{
   Add-Type -TypeDefinition $script:BaseClassDefinitions
}

#########################################################
# Generates PowerShell module containing client side 
# proxy cmdlets that can be used to interact with an 
# OData based server side endpoint.
######################################################### 
function ExportODataEndpointProxy 
{
    param
    (
        [string] $Uri,
        [string] $OutputModule,
        [string] $MetadataUri,
        [PSCredential] $Credential,
        [string] $CreateRequestMethod,
        [string] $UpdateRequestMethod,
        [string] $CmdletAdapter,
        [Hashtable] $ResourceNameMapping,
        [switch] $Force,
        [Hashtable] $CustomData,
        [switch] $AllowClobber,
        [switch] $AllowUnsecureConnection,
        [Hashtable] $Headers,
        [switch] $SkipCertificateCheck,
        [string] $ProgressBarStatus,
        [System.Management.Automation.PSCmdlet] $PSCmdlet
    )

    $script:V4Adapter = $true

    # Record of all metadata XML files which have been opened for parsing
    # used to avoid parsing the same file twice, if referenced in multiple
    # metadata files
    $script:processedFiles = @()
    
    # Record of all referenced and parsed metadata files (including entry point metadata)  
    $script:GlobalMetadata = New-Object System.Collections.ArrayList

    # The namespace name might have invalid characters or might be conflicting with class names in inheritance scenarios
    # We will be normalizing these namespaces and saving them into $normalizedNamespaces, where key is the original namespace and value is normalized namespace
    $script:normalizedNamespaces = @{}

    # This information will be used during recursive referenced metadata files loading
    $ODataEndpointProxyParameters = [ODataUtils.ODataEndpointProxyParameters] @{
        "MetadataUri" = $MetadataUri;
        "Uri" = $Uri;
        "Credential" = $Credential;
        "OutputModule" = $OutputModule;
        "Force" = $Force;
        "AllowClobber" = $AllowClobber;
        "AllowUnsecureConnection" = $AllowUnsecureConnection;
    }

    $script:TypeHashtable = @{}
    $script:ActionHashtable = @{}

    # Recursively fetch all metadatas (referenced by entry point metadata)
    GetTypeInfo -callerPSCmdlet $pscmdlet -MetadataUri $MetadataUri -ODataEndpointProxyParameters $ODataEndpointProxyParameters -Headers $Headers -SkipCertificateCheck $SkipCertificateCheck

    # Get Uri Resource path key format. It can be either 'EmbeddedKey' or 'SeparateKey'. 
    # If not provided, deault value will be set to 'EmbeddedKey'.
    $UriResourcePathKeyFormat = 'EmbeddedKey'
    if ($CustomData -and $CustomData.ContainsKey("UriResourcePathKeyFormat"))
    {
        $UriResourcePathKeyFormat = $CustomData."UriResourcePathKeyFormat"
    }

    GenerateClientSideProxyModule $GlobalMetadata $ODataEndpointProxyParameters $OutputModule $CreateRequestMethod $UpdateRequestMethod $CmdletAdapter $ResourceNameMapping $CustomData $UriResourcePathKeyFormat $ProgressBarStatus $script:normalizedNamespaces
}

#########################################################
# GetTypeInfo is a helper method used to get all the types 
# from metadata files in a recursive manner
#########################################################
function GetTypeInfo 
{
    param
    (
        [System.Management.Automation.PSCmdlet] $callerPSCmdlet,
        [string] $MetadataUri,
        [ODataUtils.ODataEndpointProxyParameters] $ODataEndpointProxyParameters,
        [Hashtable] $Headers,
        [bool] $SkipCertificateCheck
    )

    if($callerPSCmdlet -eq $null) { throw ($LocalizedData.ArguementNullError -f "callerPSCmdlet", "GetTypeInfo") }

    $metadataSet = New-Object System.Collections.ArrayList
    $metadataXML = GetMetaData $MetadataUri $callerPSCmdlet $ODataEndpointProxyParameters.Credential $Headers $SkipCertificateCheck $ODataEndpointProxyParameters.AllowUnsecureConnection
    $metadatahostUri = [uri]([Uri]$MetadataUri).GetComponents([UriComponents]::SchemeAndServer, [UriFormat]::SafeUnescaped)

    $fileName = $MetadataUri.Split('/') | select -Last 1
    $script:processedFiles += $fileName
    
    # parses all referenced metadata XML files recursively
    foreach ($reference in $metadataXML.Edmx.Reference) 
    {
        [Uri]$referenceUri = $reference.Uri
        if (-not $referenceUri.IsAbsoluteUri)
        {
            $referenceUri = New-Object System.Uri($metadatahostUri, $reference.Uri)
        }

        $referenceFileName = ([string]$referenceUri).Split('/') | select -Last 1

        if (-not $script:processedFiles.Contains([string]$referenceFileName)) 
        {
            GetTypeInfo -callerPSCmdlet $callerPSCmdlet -MetadataUri $referenceUri -ODataEndpointProxyParameters $ODataEndpointProxyParameters -Headers $Headers -SkipCertificateCheck $SkipCertificateCheck
        }
    }

    ParseMetadata -MetadataXML $metadataXML -ODataVersion $metadataXML.Edmx.Version -MetadataUri $MetadataUri -Uri $ODataEndpointProxyParameters.Uri
}

function AddMetadataToMetadataSet
{
    param
    (
        [System.Collections.ArrayList] $Metadatas,
        $NewMetadata
    )

    if($NewMetadata -eq $null) { throw ($LocalizedData.ArguementNullError -f "NewMetadata", "AddMetadataToMetadataSet") }

    if ($NewMetadata.GetType().Name -eq 'MetadataV4')
    {
        $Metadatas.Add($NewMetadata) | Out-Null
    }
    else
    {
        $Metadatas.AddRange($NewMetadata) | Out-Null
    }
}

function NormalizeNamespaceCollisionWithClassName
{
    param
    (
        [string] $InheritingType,
        [string] $BaseTypeName,
        [string] $MetadataUri
    )

    if (![string]::IsNullOrEmpty($BaseTypeName))
    {
        $dotNetNamespace = ''
        if ($BaseTypeName.LastIndexOf(".") -gt 0)
        {
            # BaseTypeStr contains Namespace and TypeName. Extract Namespace name.
            $dotNetNamespace = $BaseTypeName.SubString(0, $BaseTypeName.LastIndexOf("."))
        }
    }
}

#########################################################
# This helper method is used by functions, 
# writing directly to CDXML files or to .Net namespace/class definitions CompplexTypes file
#########################################################
function GetNamespace
{
    param
    (
        [string] $Namespace,
        $NormalizedNamespaces,
        [boolean] $isClassNameIncluded = $false
    )

    $dotNetNamespace = $Namespace
    $dotNetClassName = ''

    # Extract only namespace name
    if ($isClassNameIncluded)
    {
        if ($Namespace.LastIndexOf(".") -gt 0)
        {
            # For example, from following namespace (Namespace.TypeName) Service.1.0.0.Service we'll extract only namespace name, which is Service.1.0.0 
            $dotNetNamespace = $Namespace.SubString(0, $Namespace.LastIndexOf("."))
            $dotNetClassName = $Namespace.SubString($Namespace.LastIndexOf(".") + 1, $Namespace.Length - $Namespace.LastIndexOf(".") - 1) 
        }    
    }

    # Check if the namespace has to be normalized.
    if ($NormalizedNamespaces.ContainsKey($dotNetNamespace))
    {
        $dotNetNamespace = $NormalizedNamespaces.Get_Item($dotNetNamespace)
    }
    
    if (![string]::IsNullOrEmpty($dotNetClassName))
    {
        return ($dotNetNamespace + "." + $dotNetClassName)
    }
    else 
    {
        return $dotNetNamespace
    }
}

function NormalizeNamespaceHelper 
{
    param
    (
        [string] $Namespace,
        [boolean] $DoesNamespaceContainsInvalidChars,
        [boolean] $DoesNamespaceConflictsWithClassName
    )

    # For example, following namespace: Service.1.0.0
    # Will change to: Service_1_0_0
    # Ns postfix in Namespace name will allow to diffirintiate between this namespace 
    # and a colliding type name from different namespace
    $updatedNs = $Namespace
    if ($DoesNamespaceContainsInvalidChars)
    {
        $updatedNs = $updatedNs.Replace('.', '_')
    }
    if ($DoesNamespaceConflictsWithClassName)
    {
        $updatedNs = $updatedNs + "Ns"
    }

    $updatedNs
}

#########################################################
# Processes EntityTypes (OData V4 schema) from plain text 
# xml metadata into our custom structure
#########################################################
function ParseEntityTypes
{
    param
    (
        [System.Xml.XmlElement] $SchemaXML,
        [ODataUtils.MetadataV4] $Metadata,
        [System.Collections.ArrayList] $GlobalMetadata,
        [hashtable] $EntityAndComplexTypesQueue,
        [string] $CustomNamespace,
        [AllowEmptyString()]
        [string] $Alias
    )

    if($SchemaXML -eq $null) { throw ($LocalizedData.ArguementNullError -f "SchemaXML", "ParseEntityTypes") }

    foreach ($entityType in $SchemaXML.EntityType)
    {
        $baseType = $null

        if ($entityType.BaseType -ne $null)
        {
            # add it to the processing queue
            $baseType = GetBaseType $entityType $Metadata $SchemaXML.Namespace $GlobalMetadata
            if ($baseType -eq $null)
            {
                $EntityAndComplexTypesQueue[$entityType.BaseType] += @(@{type='EntityType'; value=$entityType})
            }
        }
        
        [ODataUtils.EntityTypeV4] $newType = ParseMetadataTypeDefinition $entityType $baseType $Metadata $schema.Namespace $Alias $true $entityType.BaseType
        $Metadata.EntityTypes += $newType
        AddDerivedTypes $newType $entityAndComplexTypesQueue $Metadata $SchemaXML.Namespace
    }
}

#########################################################
# Processes ComplexTypes from plain text xml metadata 
# into our custom structure
#########################################################
function ParseComplexTypes
{
    param
    (
        [System.Xml.XmlElement] $SchemaXML,
        [ODataUtils.MetadataV4] $Metadata,
        [System.Collections.ArrayList] $GlobalMetadata,
        [hashtable] $EntityAndComplexTypesQueue,
        [string] $CustomNamespace,
        [AllowEmptyString()]
        [string] $Alias
    )

    if($SchemaXML -eq $null) { throw ($LocalizedData.ArguementNullError -f "SchemaXML", "ParseComplexTypes") }
    
    foreach ($complexType in $SchemaXML.ComplexType)
    {
        $baseType = $null

        if ($complexType.BaseType -ne $null)
        {
            # add it to the processing queue
            $baseType = GetBaseType $complexType $metadata $SchemaXML.Namespace $GlobalMetadata
            if ($baseType -eq $null -and $entityAndComplexTypesQueue -ne $null -and $entityAndComplexTypesQueue.ContainsKey($complexType.BaseType))
            {
                $entityAndComplexTypesQueue[$complexType.BaseType] += @(@{type='ComplexType'; value=$complexType})
                continue
            }
        }

        [ODataUtils.EntityTypeV4] $newType = ParseMetadataTypeDefinition $complexType $baseType $Metadata $schema.Namespace -Alias $Alias $false $complexType.BaseType
        $Metadata.ComplexTypes += $newType
        AddDerivedTypes $newType $entityAndComplexTypesQueue $metadata $schema.Namespace
    }
}

#########################################################
# Processes TypeDefinition from plain text xml metadata 
# into our custom structure
#########################################################
function ParseTypeDefinitions
{
    param
    (
        [System.Xml.XmlElement] $SchemaXML,
        [ODataUtils.MetadataV4] $Metadata,
        [System.Collections.ArrayList] $GlobalMetadata,
        [string] $CustomNamespace,
        [AllowEmptyString()]
        [string] $Alias
    )
    
    if($SchemaXML -eq $null) { throw ($LocalizedData.ArguementNullError -f "SchemaXML", "ParseTypeDefinitions") }
    

    foreach ($typeDefinition in $SchemaXML.TypeDefinition)
    {
        $IsReadOnly = $false
        $Permissions = ($typeDefinition.Annotation | ?{$_.Term -eq "OData.Permissions"}).EnumMember
        if ($Permissions) { $IsReadOnly = $Permissions.EndsWith('/Read') }

        $newType = [ODataUtils.EntityTypeV4] @{
            "Namespace" = $Metadata.Namespace;
            "Alias" = $Metadata.Alias;
            "Name" = $typeDefinition.Name;
            "BaseTypeStr" = $typeDefinition.UnderlyingType;
            "IsReadOnly" = $IsReadOnly;
        }
        $Metadata.TypeDefinitions += $newType
    }
}

#########################################################
# Processes EnumTypes from plain text xml metadata 
# into our custom structure
#########################################################
function ParseEnumTypes
{
    param
    (
        [System.Xml.XmlElement] $SchemaXML,
        [ODataUtils.MetadataV4] $Metadata
    )

    if($SchemaXML -eq $null) { throw ($LocalizedData.ArguementNullError -f "SchemaXML", "ParseEnumTypes") }
    
    foreach ($enum in $SchemaXML.EnumType)
    {        
        $newEnumType = [ODataUtils.EnumType] @{
            "Namespace" = $Metadata.Namespace;
            "Alias" = $Metadata.Alias;
            "Name" = $enum.Name;
            "UnderlyingType" = $enum.UnderlyingType;
            "IsFlags" = $enum.IsFlags;
            "Members" = @()
        }

        if (!$newEnumType.UnderlyingType)
        {
            # If no type specified set the default type which is Edm.Int32
            $newEnumType.UnderlyingType = "Edm.Int32" 
        }

        if ($newEnumType.IsFlags -eq $null)
        {
            # If no value is specified for IsFlags, its value defaults to false.
            $newEnumType.IsFlags = $false
        }

        $enumValue = 0
        $currentEnumValue = 0

        # Now parse EnumType elements
        foreach ($element in $enum.Member)
        {
                    
            if ($element.Value -eq "" -and $newEnumType.IsFlags -eq $true)
            {
                # When IsFlags set to true each edm:Member element MUST specify a non-negative integer Value in the value attribute
                $errorMessage = ($LocalizedData.InValidMetadata)
                $detailedErrorMessage = "When IsFlags set to true each edm:Member element MUST specify a non-negative integer Value in the value attribute in " + $newEnumType.Name + " EnumType"
                $exception = [System.InvalidOperationException]::new($errorMessage, $detailedErrorMessage)
                $errorRecord = CreateErrorRecordHelper "InValidMetadata" $null ([System.Management.Automation.ErrorCategory]::InvalidData) $detailedErrorMessage nu
                $PSCmdlet.ThrowTerminatingError($errorRecord)
            }
            elseif (($element.Value -eq $null) -or ($element.Value.GetType().Name -eq "Int32" -and $element.Value -eq ""))
            {
                # If no values are specified, the members are assigned consecutive integer values in the order of their appearance, 
                # starting with zero for the first member.
                $currentEnumValue = $enumValue
            }
            else
            {
                $currentEnumValue = $element.Value
            }

            $tmp = [ODataUtils.EnumMember] @{
                "Name" = $element.Name;
                "Value" = $currentEnumValue;
            }

            $newEnumType.Members += $tmp
            $enumValue++
        }                
     
        $Metadata.EnumTypes += $newEnumType
    }
}

#########################################################
# Processes SingletonTypes from plain text xml metadata 
# into our custom structure
#########################################################
function ParseSingletonTypes
{
    param
    (
        [System.Xml.XmlElement] $SchemaEntityContainerXML,
        [ODataUtils.MetadataV4] $Metadata
    )

    if($SchemaEntityContainerXML -eq $null) { throw ($LocalizedData.ArguementNullError -f "SchemaEntityContainerXML", "ParseSingletonTypes") }
    
    foreach ($singleton in $SchemaEntityContainerXML.Singleton)
    {
        $navigationPropertyBindings = @()

        foreach ($navigationPropertyBinding in $singleton.NavigationPropertyBinding)
        {            
            $tmp = [ODataUtils.NavigationPropertyBinding] @{
                "Path" = $navigationPropertyBinding.Path;
                "Target" = $navigationPropertyBinding.Target;
            }

            $navigationPropertyBindings += $tmp
        }

        $newSingletonType = [ODataUtils.SingletonType] @{
            "Namespace" = $Metadata.Namespace;
            "Alias" = $Metadata.Alias;
            "Name" = $singleton.Name;
            "Type" = $singleton.Type;
            "NavigationPropertyBindings" = $navigationPropertyBindings;
        }

        $Metadata.SingletonTypes += $newSingletonType
    }
}

#########################################################
# Processes EntitySets from plain text xml metadata 
# into our custom structure
#########################################################
function ParseEntitySets
{
    param
    (
        [System.Xml.XmlElement] $SchemaEntityContainerXML,
        [ODataUtils.MetadataV4] $Metadata,
        [string] $Namespace,
        [AllowEmptyString()]
        [string] $Alias
    )
    
    if($SchemaEntityContainerXML -eq $null) { throw ($LocalizedData.ArguementNullError -f "SchemaEntityContainerXML", "ParseEntitySets") }

    $entityTypeToEntitySetMapping = @{};
    foreach ($entitySet in $SchemaEntityContainerXML.EntitySet)
    {
        $entityType = $metadata.EntityTypes | Where-Object { $_.Name -eq $entitySet.EntityType.Split('.')[-1] }
        $entityTypeName = $entityType.Name

        if($entityTypeToEntitySetMapping.ContainsKey($entityTypeName))
        {
            $existingEntitySetName = $entityTypeToEntitySetMapping[$entityTypeName]
            throw ($LocalizedData.EntityNameConflictError -f $entityTypeName, $existingEntitySetName, $entitySet.Name, $entityTypeName )
        }
        else
        {
            $entityTypeToEntitySetMapping.Add($entityTypeName, $entitySet.Name)
        }

        $newEntitySet = [ODataUtils.EntitySetV4] @{
            "Namespace" = $Namespace;
            "Alias" = $Alias;
            "Name" = $entitySet.Name;
            "Type" = $entityType;
        }
        
        $Metadata.EntitySets += $newEntitySet
    }
}

#########################################################
# Processes Actions from plain text xml metadata 
# into our custom structure
#########################################################
function ParseActions
{
    param
    (
        [System.Object[]] $SchemaActionsXML,
        [ODataUtils.MetadataV4] $Metadata
    )

    if($SchemaActionsXML -eq $null) { throw ($LocalizedData.ArguementNullError -f "SchemaActionsXML", "ParseActions") }
    
    foreach ($action in $SchemaActionsXML)
    {
        # HttpMethod is only used for legacy Service Operations
        if ($action.HttpMethod -eq $null)
        {
            $newAction = [ODataUtils.ActionV4] @{
                "Namespace" = $Metadata.Namespace;
                "Alias" = $Metadata.Alias;
                "Name" = $action.Name;
                "Action" = $Metadata.Namespace + '.' + $action.Name;
            }
                
            # Actions are always SideEffecting, otherwise it's an OData function
            foreach ($parameter in $action.Parameter)
            {
                if ($parameter.Nullable -ne $null)
                {
                    $parameterIsNullable = [System.Convert]::ToBoolean($parameter.Nullable);
                }
                else
                {
                    $parameterIsNullable = $true
                }

                $newParameter = [ODataUtils.TypeProperty] @{
                    "Name" = $parameter.Name;
                    "TypeName" = $parameter.Type;
                    "IsNullable" = $parameterIsNullable;
                }

                $newAction.Parameters += $newParameter
            }

            if ($action.EntitySet -ne $null)
            {
                $newAction.EntitySet = $metadata.EntitySets | Where-Object { $_.Name -eq $action.EntitySet }
            }

            $Metadata.Actions += $newAction
        }
    }
}

#########################################################
# Processes Functions from plain text xml metadata 
# into our custom structure
#########################################################
function ParseFunctions
{
    param
    (
        [System.Object[]] $SchemaFunctionsXML,
        [ODataUtils.MetadataV4] $Metadata
    )

    if($SchemaFunctionsXML -eq $null) { throw ($LocalizedData.ArguementNullError -f "SchemaFunctionsXML", "ParseFunctions") }
    
    foreach ($function in $SchemaFunctionsXML)
    {
        # HttpMethod is only used for legacy Service Operations
        if ($function.HttpMethod -eq $null)
        {
            $newFunction = [ODataUtils.FunctionV4] @{
                "Namespace" = $Metadata.Namespace;
                "Alias" = $Metadata.Alias;
                "Name" = $function.Name;
                "Function" = $Metadata.Namespace + '.' + $function.Name;
                "EntitySet" = $function.EntitySetPath;
                "ReturnType" = $function.ReturnType;
            }

            # Future TODO - consider removing this hack once all the service we run against fix this issue
            # Hack - sometimes service does not return ReturnType, however this information can be found in InnerXml
            if ($newFunction.ReturnType -eq '' -or $newFunction.ReturnType -eq 'System.Xml.XmlElement')
            {
                try
                {
                    [xml] $innerXML = '<Params>' + $function.InnerXml + '</Params>'
                    $newFunction.Returntype = $innerXML.Params.ReturnType.Type
                }
                catch
                {
                    # Do nothing
                }
            }

            # Keep only EntityType name
            $newFunction.ReturnType = $newFunction.ReturnType.Replace('Collection(', '')
            $newFunction.ReturnType = $newFunction.ReturnType.Replace(')', '')

            # Actions are always SideEffecting, otherwise it's an OData function
            foreach ($parameter in $function.Parameter)
            {
                if ($parameter.Nullable -ne $null)
                {
                    $parameterIsNullable = [System.Convert]::ToBoolean($parameter.Nullable);
                }

                $newParameter = [ODataUtils.Parameter] @{
                    "Name" = $parameter.Name;
                    "Type" = $parameter.Type;
                    "Nullable" = $parameterIsNullable;
                }

                $newFunction.Parameters += $newParameter
            }

            $Metadata.Functions += $newFunction
        }
    }
}

#########################################################
# Processes plain text xml metadata (OData V4 schema version) into our custom structure
# MetadataSet contains all parsed so far referenced Metadatas (for base class lookup)
#########################################################
function ParseMetadata 
{
    param
    (
        [xml] $MetadataXML,
        [string] $ODataVersion,
        [string] $MetadataUri,
        [string] $Uri
    )

    if($MetadataXML -eq $null) { throw ($LocalizedData.ArguementNullError -f "MetadataXML", "ParseMetadata") }

    # This is a processing queue for those types that require base types that haven't been defined yet
    $entityAndComplexTypesQueue = @{}
    [System.Collections.ArrayList] $metadatas = [System.Collections.ArrayList]::new()

    foreach ($schema in $MetadataXML.Edmx.DataServices.Schema)
    {
        if ($schema -eq $null)
        {
            Write-Error $LocalizedData.EmptySchema
            continue
        }

        [ODataUtils.MetadataV4] $metadata = [ODataUtils.MetadataV4]::new()
        $metadata.ODataVersion = $ODataVersion
        $metadata.MetadataUri = $MetadataUri
        $metadata.Uri = $Uri
        $metadata.Namespace = $schema.Namespace
        $metadata.Alias = $schema.Alias

        ParseEntityTypes -SchemaXML $schema -metadata $metadata -GlobalMetadata $script:GlobalMetadata -EntityAndComplexTypesQueue $entityAndComplexTypesQueue -CustomNamespace $CustomNamespace -Alias $metadata.Alias
        ParseComplexTypes -SchemaXML $schema -metadata $metadata -GlobalMetadata $script:GlobalMetadata -EntityAndComplexTypesQueue $entityAndComplexTypesQueue -CustomNamespace $CustomNamespace -Alias $metadata.Alias
        ParseTypeDefinitions -SchemaXML $schema -metadata $metadata -GlobalMetadata $script:GlobalMetadata -CustomNamespace $CustomNamespace -Alias $metadata.Alias
        ParseEnumTypes -SchemaXML $schema -metadata $metadata

        foreach($t in $metadata.EntityTypes)
        {
            $key = $t.Namespace + '.' + $t.Name
            $script:TypeHashtable[$key] = $t
        }
        foreach($t in $metadata.ComplexTypes)
        {
            $key = $t.Namespace + '.' + $t.Name
            $script:TypeHashtable[$key] = $t
        }
        foreach($t in $metadata.TypeDefinitions)
        {
            $key = $t.Namespace + '.' + $t.Name
            $script:TypeHashtable[$key] = $t
        }

        foreach ($entityContainer in $schema.EntityContainer)
        {
            if ($entityContainer.IsDefaultEntityContainer)
            {
                $metadata.DefaultEntityContainerName = $entityContainer.Name
            }

            ParseSingletonTypes -SchemaEntityContainerXML $entityContainer -Metadata $metadata
            ParseEntitySets -SchemaEntityContainerXML $entityContainer -Metadata $metadata -Namespace $schema.Namespace -Alias $schema.Alias
        }

        if ($schema.Action)
        {
            ParseActions -SchemaActionsXML $schema.Action -Metadata $metadata
            foreach($Action in $metadata.Actions)
            {
                $targetTypeName = $Action.Parameters[0].TypeName
                $key = $targetTypeName
                $script:ActionHashtable[$key] = $Action
            }
        }

        if ($schema.Function)
        {
            ParseFunctions -SchemaFunctionsXML $schema.Function -Metadata $metadata
        }

        $script:GlobalMetadata.Add($metadata) | Out-Null
    }
}

#########################################################
# Takes xml definition of a class from metadata document, 
# plus existing metadata structure and finds its base class
#########################################################
function GetBaseType 
{
    param
    (
        [System.Xml.XmlElement] $MetadataEntityDefinition,
        [ODataUtils.MetadataV4] $Metadata,
        [string] $Namespace,
        [System.Collections.ArrayList] $GlobalMetadata
    )

    if ($metadataEntityDefinition -ne $null -and 
        $metaData -ne $null -and 
        $MetadataEntityDefinition.BaseType -ne $null)
    {
        $baseType = $Metadata.EntityTypes | Where { $_.Namespace + "." + $_.Name -eq $MetadataEntityDefinition.BaseType -or $_.Alias + "." + $_.Name -eq $MetadataEntityDefinition.BaseType }
        if ($baseType -eq $null)
        {
            $baseType = $Metadata.ComplexTypes | Where { $_.Namespace + "." + $_.Name -eq $MetadataEntityDefinition.BaseType -or $_.Alias + "." + $_.Name -eq $MetadataEntityDefinition.BaseType }
        }

        if ($baseType -eq $null)
        {
            # Look in other metadatas, since the class can be defined in referenced metadata
            foreach ($referencedMetadata in $GlobalMetadata)
            {
                if (($baseType = $referencedMetadata.EntityTypes | Where { $_.Namespace + "." + $_.Name -eq $MetadataEntityDefinition.BaseType -or $_.Alias + "." + $_.Name -eq $MetadataEntityDefinition.BaseType }) -ne $null -or
                    ($baseType = $referencedMetadata.ComplexTypes | Where { $_.Namespace + "." + $_.Name -eq $MetadataEntityDefinition.BaseType -or $_.Alias + "." + $_.Name -eq $MetadataEntityDefinition.BaseType }) -ne $null)
                {
                    # Found base class
                    break
                }
            }
        }
    }

    if ($baseType -ne $null)
    {
        $baseType[0]
    }
}

#########################################################
# Takes base class name and global metadata structure 
# and finds its base class
#########################################################
function GetBaseTypeByName 
{
    param
    (
        [String] $BaseTypeStr,
        [System.Collections.ArrayList] $GlobalMetadata
    )

    if ($BaseTypeStr -ne $null)
    {
        
        # Look for base class definition in all referenced metadatas (including entry point)
        foreach ($referencedMetadata in $GlobalMetadata)
        {
            if (($baseType = $referencedMetadata.EntityTypes | Where { $_.Namespace + "." + $_.Name -eq $BaseTypeStr -or $_.Alias + "." + $_.Name -eq $BaseTypeStr }) -ne $null -or
                ($baseType = $referencedMetadata.ComplexTypes | Where { $_.Namespace + "." + $_.Name -eq $BaseTypeStr -or $_.Alias + "." + $_.Name -eq $BaseTypeStr }) -ne $null)
            {
                # Found base class
                break
            }
        }
    }

    if ($baseType -ne $null)
    {
        $baseType[0]
    }
    else
    { 
        $null
    }
}

#########################################################
# Processes derived types of a newly added type, 
# that were previously waiting in the queue
#########################################################
function AddDerivedTypes {
    param(
    [ODataUtils.EntityTypeV4] $baseType,
    $entityAndComplexTypesQueue,
    [ODataUtils.MetadataV4] $metadata,
    [string] $namespace
    )

    if($baseType -eq $null) { throw ($LocalizedData.ArguementNullError -f "BaseType", "AddDerivedTypes") }
    if($entityAndComplexTypesQueue -eq $null) { throw ($LocalizedData.ArguementNullError -f "EntityAndComplexTypesQueue", "AddDerivedTypes") }
    if($namespace -eq $null) { throw ($LocalizedData.ArguementNullError -f "Namespace", "AddDerivedTypes") }

    $baseTypeFullName = $baseType.Namespace + '.' + $baseType.Name
    $baseTypeShortName = $baseType.Alias + '.' + $baseType.Name

    if ($entityAndComplexTypesQueue.ContainsKey($baseTypeFullName) -or $entityAndComplexTypesQueue.ContainsKey($baseTypeShortName))
    {
        $types = $entityAndComplexTypesQueue[$baseTypeFullName] + $entityAndComplexTypesQueue[$baseTypeShortName]
        
        foreach ($type in $types)
        {
            if ($type.type -eq 'EntityType')
            {
                $newType = ParseMetadataTypeDefinition ($type.value) $baseType $metadata $namespace $true
                $metadata.EntityTypes += $newType
            }
            else
            {
                $newType = ParseMetadataTypeDefinition ($type.value) $baseType $metadata $namespace $false
                $metadata.ComplexTypes += $newType
            }

            AddDerivedTypes $newType $entityAndComplexTypesQueue $metadata $namespace
        }
    }
}

#########################################################
# Parses types definitions element of metadata xml
#########################################################
function ParseMetadataTypeDefinitionHelper 
{
    param
    (
        [System.Xml.XmlElement] $metadataEntityDefinition,
        [ODataUtils.EntityTypeV4] $baseType,
        [string] $baseTypeStr,
        [ODataUtils.MetadataV4] $metadata,
        [string] $namespace,
        [AllowEmptyString()]
        [string] $alias,
        [bool] $isEntity
    )
    
    if($metadataEntityDefinition -eq $null) { throw ($LocalizedData.ArguementNullError -f "MetadataEntityDefinition", "ParseMetadataTypeDefinition") }
    if($namespace -eq $null) { throw ($LocalizedData.ArguementNullError -f "Namespace", "ParseMetadataTypeDefinition") }

    [ODataUtils.EntityTypeRedfish] $newEntityType = CreateNewEntityType -metadataEntityDefinition $metadataEntityDefinition -baseType $baseType -baseTypeStr $baseTypeStr -namespace $namespace -alias $alias -isEntity $isEntity

    if ($baseType -ne $null)
    {
        # Add properties inherited from BaseType
        ParseMetadataBaseTypeDefinitionHelper $newEntityType $baseType
    }

    # properties defined on EntityType
    $newEntityType.EntityProperties += $metadataEntityDefinition.Property | % {
        if ($_ -ne $null)
        {
            if ($_.Nullable -ne $null)
            {
                $newPropertyIsNullable = [System.Convert]::ToBoolean($_.Nullable)
            }
            else
            {
                $newPropertyIsNullable = $true
            }

            $newPropertyIsReadOnly = $false
            $Permissions = ($_.Annotation | ?{$_.Term -eq "OData.Permissions"}).EnumMember
            if ($Permissions) { $newPropertyIsReadOnly = $Permissions.EndsWith('/Read') }

            $newPropertyIsRequiredOnCreate = ($_.Annotation | ?{$_.Term -eq "Redfish.RequiredOnCreate"}) -ne $null

            [ODataUtils.TypeProperty] @{
                "Name" = $_.Name;
                "TypeName" = $_.Type;
                "IsNullable" = $newPropertyIsNullable;
                "IsReadOnly" = $newPropertyIsReadOnly;
                "IsRequiredOnCreate" = $newPropertyIsRequiredOnCreate;
            }
        }
    }

    # odataId property will be inherited from base type, if it exists.
    # Otherwise, it should be added to current type 
    if ($baseType -eq $null)
    {
        # @odata.Id property (renamed to odataId) is required for dynamic Uri creation
        # This property is only available when user executes auto-generated cmdlet with -AllowAdditionalData, 
        # but ODataAdapter needs it to construct Uri to access navigation properties. 
        # Thus, we need to fetch this info for scenario when -AllowAdditionalData isn't used.
        $newEntityType.EntityProperties += [ODataUtils.TypeProperty] @{
                "Name" = "OdataId";
                "TypeName" = "Edm.String";
                "IsNullable" = $True;
                "IsMandatory" = $True;
            }
    }

    # Property name can't be identical to entity type name. 
    # If such property exists, "Property" suffix will be added to its name. 
    foreach ($property in $newEntityType.EntityProperties)
    {
        if ($property.Name -eq $newEntityType.Name)
        {
            $property.Name += "Property"
        }
    }

    if ($metadataEntityDefinition -ne $null -and $metadataEntityDefinition.Key -ne $null)
    {
        foreach ($entityTypeKey in $metadataEntityDefinition.Key.PropertyRef)
        {
            ($newEntityType.EntityProperties | Where-Object { $_.Name -eq $entityTypeKey.Name }).IsKey = $true
        }
    }

    $newEntityType
}

#########################################################
# Add base class entity and navigation properties to inheriting class
#########################################################
function ParseMetadataBaseTypeDefinitionHelper
{
    param
    (
        [ODataUtils.EntityTypeV4] $EntityType,
        [ODataUtils.EntityTypeV4] $BaseType
    )

    if ($EntityType -ne $null -and $BaseType -ne $null)
    {
        # Add properties inherited from BaseType
        $EntityType.EntityProperties += $BaseType.EntityProperties
        $EntityType.NavigationProperties += $BaseType.NavigationProperties
    }
}

#########################################################
# Create new EntityType object
#########################################################
function CreateNewEntityType
{
    param
    (
        [System.Xml.XmlElement] $metadataEntityDefinition,
        [ODataUtils.EntityTypeV4] $baseType,
        [string] $baseTypeStr,
        [string] $namespace,
        [AllowEmptyString()]
        [string] $alias,
        [bool] $isEntity
    )
    $newEntityType = [ODataUtils.EntityTypeRedfish] @{
        "Namespace" = $namespace;
        "Alias" = $alias;
        "Name" = $metadataEntityDefinition.Name;
        "IsEntity" = $isEntity;
        "BaseType" = $baseType;
        "BaseTypeStr" = $baseTypeStr;
    }

    $newEntityType
}

#########################################################
# Parses navigation properties from metadata xml
#########################################################
function ParseMetadataTypeDefinitionNavigationProperties
{
    param
    (
        [System.Xml.XmlElement] $metadataEntityDefinition,
        [ODataUtils.EntityTypeV4] $entityType
    )

    # navigation properties defined on EntityType
    $newEntityType.NavigationProperties = @{}
    $newEntityType.NavigationProperties.Clear()

    foreach ($navigationProperty in $metadataEntityDefinition.NavigationProperty)
    {
        $tmp = [ODataUtils.NavigationPropertyV4] @{
                "Name" = $navigationProperty.Name;
                "Type" = $navigationProperty.Type;
                "Nullable" = $navigationProperty.Nullable;
                "Partner" = $navigationProperty.Partner;
                "ContainsTarget" = $navigationProperty.ContainsTarget;
                "OnDelete" = $navigationProperty.OnDelete;
            }

        $referentialConstraints = @{}
        foreach ($constraint in $navigationProperty.ReferentialConstraints)
        {
            $tmp = [ODataUtils.ReferencedConstraint] @{
                "Property" = $constraint.Property;
                "ReferencedProperty" = $constraint.ReferencedProperty;
            }
        }

        $newEntityType.NavigationProperties += $tmp
    }
}

#########################################################
# Parses types definitions element of metadata xml for OData V4 schema
#########################################################
function ParseMetadataTypeDefinition 
{
    param
    (
        [System.Xml.XmlElement] $metadataEntityDefinition,
        [ODataUtils.EntityTypeRedfish] $baseType,
        [ODataUtils.MetadataV4] $metadata,
        [string] $namespace,
        [AllowEmptyString()]
        [string] $alias,
        [bool] $isEntity,
        [string] $baseTypeStr
    )

    if($metadataEntityDefinition -eq $null) { throw ($LocalizedData.ArguementNullError -f "MetadataEntityDefinition", "ParseMetadataTypeDefinition") }
    if($namespace -eq $null) { throw ($LocalizedData.ArguementNullError -f "Namespace", "ParseMetadataTypeDefinition") }

    [ODataUtils.EntityTypeRedfish] $newEntityType = ParseMetadataTypeDefinitionHelper -metadataEntityDefinition $metadataEntityDefinition -baseType $baseType -baseTypeStr $baseTypeStr -metadata $metadata -namespace $namespace -alias $alias -isEntity $isEntity
    if ($baseType)
    {
        $baseType.DerivedTypes += $newEntityType
    }

    $IsReadOnly = $false
    $Permissions = ($metadataEntityDefinition.Annotation | ?{$_.Term -eq "OData.Permissions"}).EnumMember
    if ($Permissions) { $IsReadOnly = $Permissions.EndsWith('/Read') }
    $newEntityType.IsReadOnly = $IsReadOnly

    $newEntityType.IsAbstract = $metadataEntityDefinition.Abstract -eq 'true'
        
    ParseMetadataTypeDefinitionNavigationProperties -metadataEntityDefinition $metadataEntityDefinition -entityType $newEntityType

    $newEntityType
}

function PrepareNavigationHashTable
{
    param
    (
        [System.Collections.ArrayList] $GlobalMetadata,
        $NormalizedNamespaces
    )

    $ht = @{}

    foreach ($Metadata in $GlobalMetadata)
    {
        foreach ($EntityType in $Metadata.EntityTypes)
        {
            foreach($np in $EntityType.NavigationProperties)
            {
                if (($np.Name) -and ($np.Type))
                {
                    #$EntityType.Namespace + "." + $EntityType.Name + "." + $np.Name  + " -> " + $np.Type;
                    $key = $np.Type;
                    $valueToAdd = $EntityType.Namespace + "|" + $EntityType.Name + "|" + $np.Name

                    # special case to avoid duplicates
                    if (($key -eq 'Resource.Item') -or ($key -eq 'Collection(Resource.Item)'))
                    {
                        continue
                    }

                    $ht[$key] += @($valueToAdd)
                }
            }
        }

    }

    return $ht
}

function GenerateServiceRootCmdlet
{
    param
    (
        [string] $OutputModule,
        [string] $HostUri
    )

    $Paths = @()

    $cmdletName = 'ServiceRoot'
            $Path = Join-Path $OutputModule "$cmdletName.cdxml"
            $Paths += $Path

            $serviceRootUri = $HostUri.TrimEnd('/') + '/redfish/v1/'
            $cdxml = StartCDXML $Path $serviceRootUri $cmdletName

            $xmlWriter = $cdxml.XmlWriter

            $xmlWriter.WriteStartElement('InstanceCmdlets')
            
            $xmlWriter.WriteStartElement('GetCmdletParameters')
            $xmlWriter.WriteAttributeString('DefaultCmdletParameterSet', "Default")

            $xmlWriter.WriteStartElement('QueryableProperties')

            $queryParameters = 
            @{
                "Select" = "Edm.String";
            }

                foreach($currentQueryParameter in $queryParameters.Keys)
                {
                    $xmlWriter.WriteStartElement('Property')
                    $xmlWriter.WriteAttributeString('PropertyName', "QueryOption:" + $currentQueryParameter)
                    $xmlWriter.WriteStartElement('Type')
                    $PSTypeName = Convert-RedfishTypeNameToCLRTypeName $queryParameters[$currentQueryParameter]
                    $xmlWriter.WriteAttributeString('PSType', $PSTypeName)
                    $xmlWriter.WriteEndElement()
                    $xmlWriter.WriteStartElement('RegularQuery')
                    $xmlWriter.WriteStartElement('CmdletParameterMetadata')
                    $xmlWriter.WriteAttributeString('PSName', $currentQueryParameter)

                        $xmlWriter.WriteStartElement('ValidateNotNullOrEmpty')
                        $xmlWriter.WriteEndElement()

                    $xmlWriter.WriteEndElement()
                    $xmlWriter.WriteEndElement()
                    $xmlWriter.WriteEndElement()
                }     


            $xmlWriter.WriteEndElement()
            $xmlWriter.WriteEndElement()

            $xmlWriter.WriteStartElement('GetCmdlet')
                $xmlWriter.WriteStartElement('CmdletMetadata')
                    $xmlWriter.WriteAttributeString('Verb', 'Get')
                $xmlWriter.WriteEndElement()
            $xmlWriter.WriteEndElement()

            $xmlWriter.WriteEndElement() # InstanceCmdlets

            
            $xmlWriter.WriteStartElement('CmdletAdapterPrivateData')

                $xmlWriter.WriteStartElement('Data')
                $xmlWriter.WriteAttributeString('Name', 'EntityTypeName')
                $xmlWriter.WriteString('PSCustomObject')
                $xmlWriter.WriteEndElement()

                $xmlWriter.WriteStartElement('Data')
			    $xmlWriter.WriteAttributeString('Name', 'IsSingleton')
			    $xmlWriter.WriteString("True")
			    $xmlWriter.WriteEndElement()

                $xmlWriter.WriteStartElement('Data')
                $xmlWriter.WriteAttributeString('Name', 'PassInnerException')
                $xmlWriter.WriteString('True')
                $xmlWriter.WriteEndElement()

            $xmlWriter.WriteEndElement() # CmdletAdapterPrivateData

            CloseCDXML $cdxml

    return $Paths
}


function GenerateResourceCmdlet
{
    param
    (
        [string] $OutputModule,
        [string] $HostUri
    )

    $Paths = @()

    $cmdletName = 'Resource'
    $Path = Join-Path $OutputModule "$cmdletName.cdxml"
    $Paths += $Path
            $cdxml = StartCDXML $Path $HostUri $cmdletName
            $xmlWriter = $cdxml.XmlWriter

            $xmlWriter.WriteStartElement('StaticCmdlets')
    
            $xmlWriter.WriteStartElement('Cmdlet')
            $xmlWriter.WriteStartElement('CmdletMetadata')
            $xmlWriter.WriteAttributeString('Verb', 'Remove')
            $xmlWriter.WriteAttributeString('DefaultCmdletParameterSet', 'ByOdataId')
            $xmlWriter.WriteEndElement()

                $xmlWriter.WriteStartElement('Method')
                $xmlWriter.WriteAttributeString('MethodName', 'Delete')
                $xmlWriter.WriteAttributeString('CmdletParameterSet', 'ByOdataId')
    
                $xmlWriter.WriteStartElement('Parameters')
                    
                    $xmlWriter.WriteStartElement('Parameter')
                    $xmlWriter.WriteAttributeString('ParameterName', 'ODataId')
                    $xmlWriter.WriteStartElement('Type')
                    $xmlWriter.WriteAttributeString('PSType', 'String')
                    $xmlWriter.WriteEndElement()

                    $xmlWriter.WriteStartElement('CmdletParameterMetadata')
                    $xmlWriter.WriteAttributeString('IsMandatory', 'true')
                    $xmlWriter.WriteAttributeString('ValueFromPipeline', 'true')
                    
                    $xmlWriter.WriteEndElement()
                    $xmlWriter.WriteEndElement()

                $xmlWriter.WriteEndElement() #Parameters

                $xmlWriter.WriteEndElement() #Method


                $xmlWriter.WriteStartElement('Method')
                $xmlWriter.WriteAttributeString('MethodName', 'Delete')
                $xmlWriter.WriteAttributeString('CmdletParameterSet', 'ByResourceObject')
    
                $xmlWriter.WriteStartElement('Parameters')
                    
                    $xmlWriter.WriteStartElement('Parameter')
                    $xmlWriter.WriteAttributeString('ParameterName', 'Resource')
                    $xmlWriter.WriteStartElement('Type')
                    $xmlWriter.WriteAttributeString('PSType', 'PSObject')
                    $xmlWriter.WriteEndElement()

                    $xmlWriter.WriteStartElement('CmdletParameterMetadata')
                    $xmlWriter.WriteAttributeString('IsMandatory', 'true')
                    $xmlWriter.WriteAttributeString('ValueFromPipeline', 'true')
                    
                    $xmlWriter.WriteEndElement()
                    $xmlWriter.WriteEndElement()

                $xmlWriter.WriteEndElement() #Parameters

                $xmlWriter.WriteEndElement() #Method

            $xmlWriter.WriteEndElement() #Cmdlet

        $xmlWriter.WriteEndElement() #StaticCmdlets

                    
            $xmlWriter.WriteStartElement('CmdletAdapterPrivateData')
                $xmlWriter.WriteStartElement('Data')
                $xmlWriter.WriteAttributeString('Name', 'EntityTypeName')
                $xmlWriter.WriteString('PSCustomObject')
                $xmlWriter.WriteEndElement()
                $xmlWriter.WriteStartElement('Data')
                $xmlWriter.WriteAttributeString('Name', 'PassInnerException')
                $xmlWriter.WriteString('True')
                $xmlWriter.WriteEndElement()
            $xmlWriter.WriteEndElement() # CmdletAdapterPrivateData
        
        CloseCDXML $cdxml

    return $Paths
}


function StartCDXML
{
    param
    (
        [string] $Path,
        [string] $hostUri,
        [string] $cmdletName
    )

	$xmlWriterSettings = New-Object System.Xml.XmlWriterSettings
	$xmlWriterSettings.Indent = $true

    $filestream = [system.IO.FileStream]::New($Path,"Create")
    $xmlWriter = [System.XML.XmlWriter]::Create($filestream,$xmlWriterSettings)
	
    $xmlWriter.WriteStartDocument()

    $today=Get-Date
    $xmlWriter.WriteComment("This module was autogenerated by PSODataUtils on $today.")

    $xmlWriter.WriteStartElement('PowerShellMetadata', 'http://schemas.microsoft.com/cmdlets-over-objects/2009/11')

    $xmlWriter.WriteStartElement('Class')
    $xmlWriter.WriteAttributeString('ClassName', $hostUri)
    $xmlWriter.WriteAttributeString('ClassVersion', '1.0.0')
        
    $xmlWriter.WriteAttributeString('CmdletAdapter', 'Microsoft.PowerShell.Cmdletization.OData.RedfishCmdletAdapter, PowerShell.Cmdletization.OData')

    $xmlWriter.WriteElementString('Version', '1.0')
    $xmlWriter.WriteElementString('DefaultNoun', $cmdletName)

    return [pscustomobject]@{
        XmlWriter = $xmlWriter
        FileStream = $filestream}
}


function CloseCDXML 
{
    param
    (
        $cdxml
    )

    $cdxml.XmlWriter.Flush()
    $cdxml.XmlWriter.Dispose()
    $cdxml.FileStream.Close()
}


function GenerateNavigationCmdlets
{
    param
    (
        $xmlWriter,
        $navigationHashtable,
        [string] $FullTypeName
    )
        $parents = $navigationHashtable[$FullTypeName]
        
        $isCollection = $false
        
        if ($FullTypeName.StartsWith("Collection("))
        {
            $isCollection = $true
            $FullTypeName = $FullTypeName.Replace('Collection(','').Replace(')','')
        }

            $TypeName = $FullTypeName.Split('.')|select -Last 1


            $xmlWriter.WriteStartElement('InstanceCmdlets')
            $xmlWriter.WriteStartElement('GetCmdletParameters')
            $xmlWriter.WriteAttributeString('DefaultCmdletParameterSet', "Default")
            $xmlWriter.WriteStartElement('QueryableProperties')

            foreach($parent in $parents)
            {
                $parentParts = $parent.Split('|')
                $parentNamespace = $parentParts[0]
                $parentTypeName = $parentParts[1]
                $parentPropertyName = $parentParts[2]

                $xmlWriter.WriteStartElement('Property')
                $xmlWriter.WriteAttributeString('PropertyName', $parentTypeName)

                $xmlWriter.WriteStartElement('Type')
                    $xmlWriter.WriteAttributeString('PSType', "PSCustomObject")
                $xmlWriter.WriteEndElement()

                $xmlWriter.WriteStartElement('RegularQuery')
                    $xmlWriter.WriteStartElement('CmdletParameterMetadata')
                        $xmlWriter.WriteAttributeString('PSName', $parentTypeName)
                        $xmlWriter.WriteAttributeString('CmdletParameterSets', $parentTypeName)
                        $xmlWriter.WriteAttributeString('IsMandatory', 'true')
                        $xmlWriter.WriteAttributeString('ValueFromPipeline', 'true')
                    $xmlWriter.WriteEndElement()
                $xmlWriter.WriteEndElement()

                $xmlWriter.WriteEndElement()
            }

            # Add Query Parameters (i.e., Top, Skip, OrderBy, Filter) to the generated Get-* cmdlets.
            $queryParameters = 
            @{
                "Filter" = "Edm.String";
                "IncludeTotalResponseCount" = "switch";
                "OrderBy" = "Edm.String";
                "Select" = "Edm.String";  
                "Skip" = "Edm.Int32"; 
                "Top" = "Edm.Int32";
            }

                foreach($currentQueryParameter in $queryParameters.Keys)
                {
                    $xmlWriter.WriteStartElement('Property')
                    $xmlWriter.WriteAttributeString('PropertyName', "QueryOption:" + $currentQueryParameter)
                    $xmlWriter.WriteStartElement('Type')
                    $PSTypeName = Convert-RedfishTypeNameToCLRTypeName $queryParameters[$currentQueryParameter]
                    $xmlWriter.WriteAttributeString('PSType', $PSTypeName)
                    $xmlWriter.WriteEndElement()
                    $xmlWriter.WriteStartElement('RegularQuery')
                    $xmlWriter.WriteStartElement('CmdletParameterMetadata')
                    $xmlWriter.WriteAttributeString('PSName', $currentQueryParameter)

                        $xmlWriter.WriteStartElement('ValidateNotNullOrEmpty')
                        $xmlWriter.WriteEndElement()

                    $xmlWriter.WriteEndElement()
                    $xmlWriter.WriteEndElement()
                    $xmlWriter.WriteEndElement()
                }     


            $xmlWriter.WriteEndElement() # QueryableProperties
            $xmlWriter.WriteEndElement() # GetCmdletParameters

            $xmlWriter.WriteStartElement('GetCmdlet')
                $xmlWriter.WriteStartElement('CmdletMetadata')
                    $xmlWriter.WriteAttributeString('Verb', 'Get')
                $xmlWriter.WriteEndElement()
            $xmlWriter.WriteEndElement()

            $xmlWriter.WriteEndElement() # InstanceCmdlets

            
            $navigationPrivateData = @{}
            foreach($parent in $parents)
            {
                $parentParts = $parent.Split('|')
                $parentNamespace = $parentParts[0]
                $parentTypeName = $parentParts[1]
                $parentPropertyName = $parentParts[2]

                $key = 'NavigationLink'+$parentTypeName
                if ($isCollection)
                {
                    $navigationPrivateData[$key] = $parent+"|Collection"
                }
                else
                {
                    $navigationPrivateData[$key] = $parent
                }
            }

    return $navigationPrivateData
}



function SaveCmdletAdapterPrivateData
{
    param
    (
        $xmlWriter,
        $navigationPrivateData,
        $actionTargets
    )

    $xmlWriter.WriteStartElement('CmdletAdapterPrivateData')

    $xmlWriter.WriteStartElement('Data')
    $xmlWriter.WriteAttributeString('Name', 'EntityTypeName')
    $xmlWriter.WriteString('PSCustomObject')
    $xmlWriter.WriteEndElement()

    foreach($NavigationLink in $navigationPrivateData.Keys)
    {
        $parent = $navigationPrivateData[$NavigationLink]

        $xmlWriter.WriteStartElement('Data')
        $xmlWriter.WriteAttributeString('Name', $NavigationLink)
        $xmlWriter.WriteString($parent)
        $xmlWriter.WriteEndElement()
    }

    if ($actionTargets.Count -gt 0)
    {
        $actionTargetsString = $actionTargets -join ';'
        $xmlWriter.WriteStartElement('Data')
        $xmlWriter.WriteAttributeString('Name', 'ActionTargets')
        $xmlWriter.WriteString($actionTargetsString)
        $xmlWriter.WriteEndElement()
    }

    $xmlWriter.WriteStartElement('Data')
    $xmlWriter.WriteAttributeString('Name', 'UpdateRequestMethod')
    $xmlWriter.WriteString('Patch')
    $xmlWriter.WriteEndElement()

    $xmlWriter.WriteStartElement('Data')
    $xmlWriter.WriteAttributeString('Name', 'CreateRequestMethod')
    $xmlWriter.WriteString('Post')
    $xmlWriter.WriteEndElement()

    $xmlWriter.WriteStartElement('Data')
    $xmlWriter.WriteAttributeString('Name', 'PassInnerException')
    $xmlWriter.WriteString('True')
    $xmlWriter.WriteEndElement()

    $xmlWriter.WriteEndElement() # CmdletAdapterPrivateData
}

function GetWritablePropertiesOfType
{
    param
    (
        $TypeObj,
        $PropertyList
    )

    foreach($Property in $TypeObj.EntityProperties)
    {
        if (-not $Property.IsReadOnly)
        {
            $PropertyType = $script:TypeHashtable[$Property.TypeName]
            if (-not $PropertyType.IsReadOnly)
            {
                $propertyAlreadyAdded = $PropertyList | ?{ $_.Name -eq $Property.Name }

                if (-not $propertyAlreadyAdded)
                {
                    if ($Property.Name -eq 'OdataId')
                    {
                        $PropertyList.Insert(0, $Property) | Out-Null
                    }
                    else
                    {
                        $PropertyList.Add($Property) | Out-Null
                    }
                }
            }
        }
    }

    foreach($DerivedType in $TypeObj.DerivedTypes)
    {
        GetWritablePropertiesOfType $DerivedType $PropertyList
    }
}

function GetPropertySetsOfType
{
    param
    (
        $TypeObj,
        $PropertySetsHashTable
    )

    if (-not $TypeObj.IsAbstract)
    {
        $PropertySetName = $TypeObj.Namespace + "." + $TypeObj.Name
        foreach($Property in $TypeObj.EntityProperties)
        {
            $PropertySetsHashTable[$PropertySetName] += @($Property)
        }
    }

    foreach($DerivedType in $TypeObj.DerivedTypes)
    {
        GetPropertySetsOfType $DerivedType $PropertySetsHashTable
    }
}

function GetTypesWithActions
{
    param
    (
        $TypeObj,
        $ActionsHashtable
    )

    foreach($Property in $TypeObj.EntityProperties)
    {
        if ($Property.Name -eq 'Actions')
        {

            $key = $Property.TypeName
            $value = $TypeObj

            if (-not $ActionsHashtable.ContainsKey($key))
            {
                $ActionsHashtable[$key] = $value
            }
        }
    }

    foreach($DerivedType in $TypeObj.DerivedTypes)
    {
        GetTypesWithActions $DerivedType $ActionsHashtable
    }
}


function GenerateSetCmdlets
{
    param
    (
        $GlobalMetadata,
        $xmlWriter,
        [string] $TypeName
    )


    $TypeObj = $script:TypeHashtable[$TypeName]

    $PropertyList = New-Object System.Collections.ArrayList
    GetWritablePropertiesOfType $TypeObj $PropertyList

    # we only generate Set cmdcmdlets for a type if it has any writable properties
    if ($PropertyList.Count -gt 1) # OdataId always present for all types
    {

        $xmlWriter.WriteStartElement('Cmdlet')
            $xmlWriter.WriteStartElement('CmdletMetadata')
            $xmlWriter.WriteAttributeString('Verb', 'Set')
            $xmlWriter.WriteAttributeString('DefaultCmdletParameterSet', 'Default')
            $xmlWriter.WriteEndElement()

            $xmlWriter.WriteStartElement('Method')
            $xmlWriter.WriteAttributeString('MethodName', 'Update')
            $xmlWriter.WriteAttributeString('CmdletParameterSet', 'Default')

            $xmlWriter.WriteStartElement('Parameters')
            $pos = 0

            foreach($Property in $PropertyList)
            {
                $xmlWriter.WriteStartElement('Parameter')
                $xmlWriter.WriteAttributeString('ParameterName', $Property.Name)
                $xmlWriter.WriteStartElement('Type')
                $PSTypeName = Convert-RedfishTypeNameToCLRTypeName $Property.TypeName
                $xmlWriter.WriteAttributeString('PSType', $PSTypeName)
                $xmlWriter.WriteEndElement()

                $xmlWriter.WriteStartElement('CmdletParameterMetadata')
                $xmlWriter.WriteAttributeString('PSName', $Property.Name)
                $xmlWriter.WriteAttributeString('IsMandatory', ($Property.IsMandatory).ToString().ToLowerInvariant())
                $xmlWriter.WriteAttributeString('Position', $pos)
                $xmlWriter.WriteEndElement()
                $xmlWriter.WriteEndElement()
    
                $pos++
            }
            $xmlWriter.WriteEndElement() #Parameters

            $xmlWriter.WriteEndElement() #Method
        $xmlWriter.WriteEndElement() #Cmdlet
    }
}



function GenerateNewCmdlets
{
    param
    (
        $GlobalMetadata,
        $xmlWriter,
        [string] $TypeName,
        $navigationHashtable,
        [string] $FullTypeName
    )

    # Per Redfish spec, creating new objects is valid only for collections (adding new items to collections)
    # so first we need to check if a current type has a navigation link from any collection

    $isCollection = $FullTypeName.StartsWith("Collection(")

    if ($isCollection)
    {
        $TypeObj = $script:TypeHashtable[$TypeName]

        $PropertySetsHashTable = @{}
        GetPropertySetsOfType $TypeObj $PropertySetsHashTable

        if ($PropertySetsHashTable.Keys.Count -gt 1)
        {
            $xmlWriter.WriteStartElement('Cmdlet')
            $xmlWriter.WriteStartElement('CmdletMetadata')
            $xmlWriter.WriteAttributeString('Verb', 'New')
            $xmlWriter.WriteAttributeString('DefaultCmdletParameterSet', @($PropertySetsHashTable.Keys)[0])
            $xmlWriter.WriteEndElement()

            foreach($ParameterSetName in $PropertySetsHashTable.Keys)
            {

                $xmlWriter.WriteStartElement('Method')
                $xmlWriter.WriteAttributeString('MethodName', 'Create')
                $xmlWriter.WriteAttributeString('CmdletParameterSet', $ParameterSetName)
    
                $xmlWriter.WriteStartElement('Parameters')
                foreach($Property in $PropertySetsHashTable[$ParameterSetName])
                {
                    $xmlWriter.WriteStartElement('Parameter')
                    $xmlWriter.WriteAttributeString('ParameterName', $Property.Name)
                    $xmlWriter.WriteStartElement('Type')
                    $PSTypeName = Convert-RedfishTypeNameToCLRTypeName $Property.TypeName
                    $xmlWriter.WriteAttributeString('PSType', $PSTypeName)
                    $xmlWriter.WriteEndElement()

                    $xmlWriter.WriteStartElement('CmdletParameterMetadata')
                    $xmlWriter.WriteAttributeString('IsMandatory', ($Property.IsMandatory -or $Property.IsRequiredOnCreate).ToString().ToLowerInvariant())
                    if ($Property.Name -eq 'OdataId')
                    {
                        $CollectionOdataIdName = $TypeObj.Name + "CollectionOdataId"
                        $xmlWriter.WriteAttributeString('PSName', $CollectionOdataIdName)
                    }

                    $xmlWriter.WriteEndElement()
                    $xmlWriter.WriteEndElement()
                }

                $xmlWriter.WriteEndElement() #Parameters

                $xmlWriter.WriteEndElement() #Method
            }

            $xmlWriter.WriteEndElement() #Cmdlet
        }
    }
}


function GenerateDeleteCmdlets
{
    param
    (
        $xmlWriter
    )

    $xmlWriter.WriteStartElement('Cmdlet')
        $xmlWriter.WriteStartElement('CmdletMetadata')
        $xmlWriter.WriteAttributeString('Verb', 'Remove')
        $xmlWriter.WriteAttributeString('DefaultCmdletParameterSet', 'ByOdataId')
        $xmlWriter.WriteEndElement()

        $xmlWriter.WriteStartElement('Method')
        $xmlWriter.WriteAttributeString('MethodName', 'Delete')
        $xmlWriter.WriteAttributeString('CmdletParameterSet', 'ByOdataId')
    
            $xmlWriter.WriteStartElement('Parameters')
                    
                $xmlWriter.WriteStartElement('Parameter')
                $xmlWriter.WriteAttributeString('ParameterName', 'ODataId')
                    $xmlWriter.WriteStartElement('Type')
                    $xmlWriter.WriteAttributeString('PSType', 'String')
                    $xmlWriter.WriteEndElement()

                    $xmlWriter.WriteStartElement('CmdletParameterMetadata')
                    $xmlWriter.WriteAttributeString('IsMandatory', 'true')
                    $xmlWriter.WriteAttributeString('ValueFromPipeline', 'true')
                    $xmlWriter.WriteEndElement()

                $xmlWriter.WriteEndElement() #Parameter

            $xmlWriter.WriteEndElement() #Parameters

        $xmlWriter.WriteEndElement() #Method

        $xmlWriter.WriteStartElement('Method')
        $xmlWriter.WriteAttributeString('MethodName', 'Delete')
        $xmlWriter.WriteAttributeString('CmdletParameterSet', 'ByResourceObject')
    
            $xmlWriter.WriteStartElement('Parameters')
                    
                $xmlWriter.WriteStartElement('Parameter')
                $xmlWriter.WriteAttributeString('ParameterName', 'Resource')
                    $xmlWriter.WriteStartElement('Type')
                    $xmlWriter.WriteAttributeString('PSType', 'PSObject')
                    $xmlWriter.WriteEndElement()

                    $xmlWriter.WriteStartElement('CmdletParameterMetadata')
                    $xmlWriter.WriteAttributeString('IsMandatory', 'true')
                    $xmlWriter.WriteAttributeString('ValueFromPipeline', 'true')
                    $xmlWriter.WriteEndElement()
                $xmlWriter.WriteEndElement() #Parameter

            $xmlWriter.WriteEndElement() #Parameters

        $xmlWriter.WriteEndElement() #Method

    $xmlWriter.WriteEndElement() #Cmdlet
}


function GenerateActionCmdlets
{
    param
    (
        $GlobalMetadata,
        $xmlWriter,
        [string] $TypeName,
        $navigationHashtable,
        [string] $FullTypeName
    )

    # If current type (or derived types) has 'Actions' property, then find actions definition and generate cmdlets based on that

    $TypeObj = $script:TypeHashtable[$TypeName]

    $TypesWithActions = @{}
    GetTypesWithActions $TypeObj $TypesWithActions

    $actionTargets = @()

    foreach($TargetTypeName in $TypesWithActions.Keys)
    {
        $actionDefinition = $script:ActionHashtable[$TargetTypeName]
        if ($actionDefinition)
        {
            $typeWithAction = $TypesWithActions[$TargetTypeName]

            
                $xmlWriter.WriteStartElement('Cmdlet')
                $xmlWriter.WriteStartElement('CmdletMetadata')
                $xmlWriter.WriteAttributeString('Verb', 'Invoke')
                $cmdletNoun = "$($typeWithAction.Name)$($actionDefinition.Name)"
                $xmlWriter.WriteAttributeString('Noun', $cmdletNoun)
                $xmlWriter.WriteAttributeString('DefaultCmdletParameterSet', 'ByOdataId')
                $xmlWriter.WriteEndElement()

                foreach($ParameterSet in @('ByOdataId','ByResourceObject'))
                {

                    $xmlWriter.WriteStartElement('Method')
                    $xmlWriter.WriteAttributeString('MethodName', "Action:$($actionDefinition.Action)")
                    $xmlWriter.WriteAttributeString('CmdletParameterSet', $ParameterSet)
    
                    $xmlWriter.WriteStartElement('Parameters')
                    [bool]$firstParameter = $true
                    foreach($Property in $actionDefinition.Parameters)
                    {
                        if ($firstParameter)
                        {
                            if ($ParameterSet -eq 'ByOdataId')
                            {
                                $xmlWriter.WriteStartElement('Parameter')
                                $xmlWriter.WriteAttributeString('ParameterName', 'ODataId')
                                $xmlWriter.WriteStartElement('Type')
                                $xmlWriter.WriteAttributeString('PSType', 'String')
                                $xmlWriter.WriteEndElement()

                                $xmlWriter.WriteStartElement('CmdletParameterMetadata')
                                $xmlWriter.WriteAttributeString('IsMandatory', 'true')
                                $xmlWriter.WriteAttributeString('ValueFromPipeline', 'true')
                    
                                $xmlWriter.WriteEndElement()
                                $xmlWriter.WriteEndElement()

                                $firstParameter = $false
                                continue
                            }
                        }

                        $xmlWriter.WriteStartElement('Parameter')
                        $xmlWriter.WriteAttributeString('ParameterName', $Property.Name)
                        $xmlWriter.WriteStartElement('Type')
                        $PSTypeName = Convert-RedfishTypeNameToCLRTypeName $Property.TypeName
                        $xmlWriter.WriteAttributeString('PSType', $PSTypeName)
                        $xmlWriter.WriteEndElement()

                        $xmlWriter.WriteStartElement('CmdletParameterMetadata')
                        $xmlWriter.WriteAttributeString('IsMandatory', 'true')
                        if ($firstParameter)
                        {
                            $xmlWriter.WriteAttributeString('ValueFromPipeline', 'true')
                            $firstParameter = $false

                            $actionTargets += $actionDefinition.Action+"="+$Property.Name
                        }

                        $xmlWriter.WriteEndElement()
                        $xmlWriter.WriteEndElement()
                    }

                    $xmlWriter.WriteEndElement() #Parameters

                    $xmlWriter.WriteEndElement() #Method
                }

                $xmlWriter.WriteEndElement() #Cmdlet
        }
    }
    $actionTargets
}



#########################################################
# Create psd1 and cdxml files required to auto-generate 
# cmdlets for given service.
#########################################################
function GenerateClientSideProxyModule 
{
    param
    (
        [System.Collections.ArrayList] $GlobalMetadata,
        [ODataUtils.ODataEndpointProxyParameters] $ODataEndpointProxyParameters,
        [string] $OutputModule,
        [string] $CreateRequestMethod,
        [string] $UpdateRequestMethod,
        [string] $CmdletAdapter,
        [Hashtable] $resourceNameMappings,
        [Hashtable] $CustomData,
        [string] $UriResourcePathKeyFormat,
        [string] $progressBarStatus,
        $NormalizedNamespaces
    )

    if($progressBarStatus -eq $null) { throw ($LocalizedData.ArguementNullError -f "ProgressBarStatus", "GenerateClientSideProxyModule") }

    Write-Verbose ($LocalizedData.VerboseSavingModule -f $OutputModule)
    
    $navigationHashtable = PrepareNavigationHashTable $GlobalMetadata $NormalizedNamespaces
    $hostUri = ([uri]$ODataEndpointProxyParameters.Uri).GetComponents([UriComponents]::SchemeAndServer, [UriFormat]::SafeUnescaped)

    $additionalModules = @()
    
    foreach($FullTypeName in $navigationHashtable.Keys)
    {
        $TypeName = $FullTypeName
        if ($TypeName.StartsWith("Collection("))
        {
            $TypeName = $TypeName.Replace('Collection(','').Replace(')','')
        }

        $ShortTypeName = $TypeName.Split('.')|select -Last 1
        
        $cmdletName = $ShortTypeName
        $NewModulePath = Join-Path $OutputModule "$cmdletName.cdxml"
        $additionalModules += $NewModulePath 

        $cdxml = StartCDXML $NewModulePath $hostUri $cmdletName
        $xmlWriter = $cdxml.XmlWriter

        $navigationPrivateData = GenerateNavigationCmdlets $xmlWriter $navigationHashtable $FullTypeName

        $xmlWriter.WriteStartElement('StaticCmdlets')
    
        GenerateSetCmdlets $GlobalMetadata $xmlWriter $TypeName
        GenerateNewCmdlets $GlobalMetadata $xmlWriter $TypeName $navigationHashtable $FullTypeName
        GenerateDeleteCmdlets $xmlWriter

        $actionTargets = GenerateActionCmdlets $GlobalMetadata $xmlWriter $TypeName $navigationHashtable $FullTypeName

        
        $xmlWriter.WriteEndElement() #StaticCmdlets

        SaveCmdletAdapterPrivateData $xmlWriter $navigationPrivateData $actionTargets

        CloseCDXML $cdxml
    }

    $ServiceRootModulePath = GenerateServiceRootCmdlet $OutputModule $hostUri
    $additionalModules += $ServiceRootModulePath

    $ResourceCmdletModulePath = GenerateResourceCmdlet $OutputModule $hostUri
    $additionalModules += $ResourceCmdletModulePath
    
    $additionalModulesFiles = $additionalModules | Split-Path -Leaf

    $moduleDirInfo = [System.IO.DirectoryInfo]::new($OutputModule)
    $moduleManifestName = $moduleDirInfo.Name + ".psd1"

    GenerateModuleManifest $GlobalMetadata $OutputModule\$moduleManifestName $additionalModulesFiles $resourceNameMappings $progressBarStatus
}

#########################################################
# GenerateModuleManifest is a helper function used 
# to generate a wrapper module manifest file. The
# generated module manifest is persisted to the disk at
# the specified OutputModule path. When the module 
# manifest is imported, the following comands will 
# be imported:
# 1. Get, Set, New & Remove proxy cmdlets for entity 
#    sets and singletons.
# 2. If the server side Odata endpoint exposes complex
#    types, enum types, type definitions, then the corresponding 
#    client side proxy types imported.
# 3. Service Action/Function proxy cmdlets.   
#########################################################
function GenerateModuleManifest 
{
    param
    (
        [System.Collections.ArrayList] $GlobalMetadata,
        [String] $ModulePath,
        [string[]] $AdditionalModules,
        [Hashtable] $resourceNameMappings,
        [string] $progressBarStatus
    )

    if($progressBarStatus -eq $null) { throw ($LocalizedData.ArguementNullError -f "progressBarStatus", "GenerateModuleManifest") }

    $editionSubfolder = 'FullCLR'
    if ($PSEdition -eq "Core")
    {
        $editionSubfolder = 'CoreCLR'
    }

    $ScriptDir = Split-path $PSCommandPath
    $editionFolder = join-path $ScriptDir $editionSubfolder
    [string]$CmdletizationDllPath = join-path $editionFolder 'PowerShell.Cmdletization.OData.dll'
    [string]$stub = 'CmdletizationDllPathStub'

    New-ModuleManifest -Path $ModulePath -NestedModules $AdditionalModules -RequiredAssemblies $stub
    $manifestConent = Get-Content -Path $ModulePath -Raw
    Set-Content -Path $ModulePath -Value $manifestConent.Replace($stub, $CmdletizationDllPath)

    Write-Verbose ($LocalizedData.VerboseSavedModuleManifest -f $ModulePath)

    # Update the Progress Bar.
    ProgressBarHelper "Export-ODataEndpointProxy" $progressBarStatus 80 20 1 1
}


function Convert-RedfishTypeNameToCLRTypeName
{
    param
    (
        [string] $typeName,
        [bool] $checkBaseType = $true
    )

    switch ($typeName) 
    {
        "Edm.Binary" {"Byte[]"}
        "Edm.Boolean" {"Boolean"}
        "Edm.Byte" {"Byte"}
        "Edm.DateTime" {"DateTime"}
        "Edm.Decimal" {"Decimal"}
        "Edm.Double" {"Double"}
        "Edm.Single" {"Single"}
        "Edm.Guid" {"Guid"}
        "Edm.Int16" {"Int16"}
        "Edm.Int32" {"Int32"}
        "Edm.Int64" {"Int64"}
        "Edm.SByte" {"SByte"}
        "Edm.String" {"String"}
        "Edm.PropertyPath"  {"String"}
        "switch" {"switch"}
        "Edm.DateTimeOffset" {"DateTimeOffset"}
        default 
        {
            $result = "PSObject"
            if ($checkBaseType)
            {
                $TypeObj = $script:TypeHashtable[$typeName]
                $result = Convert-RedfishTypeNameToCLRTypeName $TypeObj.BaseTypeStr $false
            }
            $result
        }
    }
}