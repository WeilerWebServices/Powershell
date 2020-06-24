<############################################################################################ 
 # File: Pester.PowerShell.ODataUtils.Tests.ps1
 # This suite contains Tests that are
 # used for validating Microsoft.PowerShell.ODataUtils module.
 ############################################################################################>
$script:TestSourceRoot = $PSScriptRoot
$ModuleBase = Split-Path $script:TestSourceRoot
$ModuleBase = Join-Path (Join-Path $ModuleBase 'src') 'ModuleGeneration'

$ODataUtilsHelperPath = Join-Path $ModuleBase 'Microsoft.PowerShell.ODataUtilsHelper.ps1'
. $ODataUtilsHelperPath

Describe "Test suite for Microsoft.PowerShell.ODataUtils module" -Tags CI {

    Context "OData validation test cases" {

        BeforeAll {
            $scriptToDotSource = Join-Path $ModuleBase 'Microsoft.PowerShell.ODataAdapter.ps1'
            . $scriptToDotSource

            $metadataXmlPath = Join-Path $script:TestSourceRoot "metadata.xml"
            $metadataXml = Get-Content $metadataXmlPath
            [xml]$xmlDoc = $metadataXml
            $ns = new-object Xml.XmlNamespaceManager $xmlDoc.NameTable
            $ns.AddNamespace("m", $xmlDoc.Edmx.DataServices.m)
            $ns.AddNamespace("ns", $xmlDoc.Edmx.DataServices.Schema.xmlns)
        }
    
        function Get-MockCmdlet {[CmdletBinding()] param()
            return $PSCmdlet
        }
        
        It "Checks type conversion to CLR types" {
            
            $ODataTypes = @{
                "Edm.Binary"="Byte[]";
                "Edm.Boolean"="Boolean";
                "Edm.Byte"="Byte";
                "Edm.DateTime"="DateTime";
                "Edm.Decimal"="Decimal";
                "Edm.Double"="Double";
                "Edm.Single"="Single";
                "Edm.Guid"="Guid";
                "Edm.Int16"="Int16";
                "Edm.Int32"="Int32";
                "Edm.Int64"="Int64";
                "Edm.SByte"="SByte";
                "Edm.String"="String"}

            foreach ($h in $ODataTypes.GetEnumerator()) 
            {
                $resultType = Convert-ODataTypeToCLRType "$($h.Name)"
                $resultType | Should Be "$($h.Value)"
            }
        }

        It "Checks collection conversion to CLR types" {
            
            Convert-ODataTypeToCLRType 'Collection(Edm.Int16)' | Should Be 'Int16[]'
            Convert-ODataTypeToCLRType 'Collection(Collection(Edm.Byte))' | Should Be 'Byte[][]'
            Convert-ODataTypeToCLRType 'Collection(Collection(Edm.Binary))' | Should Be 'Byte[][][]'
        }

        It "Checks parsing metadata" {
            
            $tmpcmdlet = Get-MockCmdlet
            $result = ParseMetadata -metadataXml $metadataXml -metaDataUri 'https://SomeUri.org' -cmdletAdapter 'ODataAdapter' -callerPSCmdlet $tmpcmdlet
            $result.Namespace | Should Be "ODataDemo"
            $result.DefaultEntityContainerName | Should Be "DemoService"

            
            $result.EntitySets.Length | Should Be @($xmlDoc.selectNodes('//ns:EntitySet', $ns)).Count
            @($result.EntitySets | ?{$_.Name -eq 'Products'}).Count | Should Be @($xmlDoc.selectNodes('//ns:EntitySet[@Name="Products"]', $ns)).Count
            
            $result.EntityTypes.Length | Should Be @($xmlDoc.selectNodes('//ns:EntityType', $ns)).Count
            @($result.EntityTypes | ?{$_.Name -eq 'Customer'}).Count | Should Be @($xmlDoc.selectNodes('//ns:EntityType[@Name="Customer"]', $ns)).Count

            $result.ComplexTypes.Length | Should Be @($xmlDoc.selectNodes('//ns:ComplexType', $ns)).Count
            @($result.ComplexTypes | ?{$_.Name -eq 'Address'}).Count | Should Be @($xmlDoc.selectNodes('//ns:ComplexType[@Name="Address"]', $ns)).Count

            $result.Associations.Length | Should Be @($xmlDoc.selectNodes('//ns:Association', $ns)).Count
            @($result.Associations | ?{$_.Name -eq 'Product_Categories_Category_Products'}).Count | Should Be @($xmlDoc.selectNodes('//ns:Association[@Name="Product_Categories_Category_Products"]', $ns)).Count

            $result.Actions.Length | Should Be @($xmlDoc.selectNodes('//ns:FunctionImport[not(@m:HttpMethod)]', $ns)).Count
            @($result.Actions | ?{$_.Verb -eq 'IncreaseSalaries'}).Count | Should Be @($xmlDoc.selectNodes('//ns:FunctionImport[@Name="IncreaseSalaries"]', $ns)).Count
        }

        It "Verifies that generated module has correct contents" {
            
            $tmpcmdlet = Get-MockCmdlet
            $metadata = ParseMetadata -metadataXml $metadataXml -metaDataUri 'https://SomeUri.org' -cmdletAdapter 'ODataAdapter' -callerPSCmdlet $tmpcmdlet

            $entitySet = $metadata.EntitySets[0]
            [string]$generatedModuleName = $entitySet.Type.Name

            $moduleDir = join-path $TestDrive "v3Module"
            New-Item $moduleDir -ItemType Directory -ErrorAction SilentlyContinue | Out-Null

            try
            {
                GenerateCRUDProxyCmdlet $entitySet $metadata 'http://fakeuri/Service.svc' $moduleDir 'Post' 'Patch' 'ODataAdapter' $null $null $null ' ' ' ' 10 5 10 1 $tmpcmdlet
            }
            catch
            {
                $_.FullyQualifiedErrorId | Should Be NotImplementedException
            }
            
            $modulepath = join-path $moduleDir $generatedModuleName
            $modulepath += ".cdxml"
            [xml]$doc = Get-Content $modulepath -Raw

            $ns = new-object Xml.XmlNamespaceManager $doc.NameTable
            $ns.AddNamespace("ns", $doc.PowerShellMetadata.xmlns)

            $queryableProperties = $doc.GetElementsByTagName("QueryableProperties")
            $queryableProperties.Count | Should Be @($doc.SelectNodes('//ns:QueryableProperties', $ns)).Count
            $queryableProperties[0].ChildNodes.Count | Should Be @($doc.SelectNodes('//ns:QueryableProperties/*', $ns)).Count
            $doc.GetElementsByTagName("QueryableAssociations").Count | Should Be @($doc.SelectNodes('//ns:QueryableAssociations', $ns)).Count
            $doc.GetElementsByTagName("GetCmdlet").Count | Should Be @($doc.SelectNodes('//ns:GetCmdlet', $ns)).Count
            $staticCmdlets = $doc.GetElementsByTagName("StaticCmdlets")
            $staticCmdlets.Count | Should Be @($doc.SelectNodes('//ns:StaticCmdlets', $ns)).Count
            $staticCmdlets[0].ChildNodes.Count | Should Be @($doc.SelectNodes('//ns:StaticCmdlets/*', $ns)).Count
            $doc.GetElementsByTagName("Cmdlet").Count | Should Be @($doc.SelectNodes('//ns:Cmdlet', $ns)).Count
            $doc.GetElementsByTagName("Method").Count | Should Be @($doc.SelectNodes('//ns:Method', $ns)).Count
        }

        It "Verifies that generated module manifest has correct amount of nested modules" {
            
            $tmpcmdlet = Get-MockCmdlet
            $metadata = ParseMetadata -metadataXml $metadataXml -metaDataUri 'https://SomeUri.org' -cmdletAdapter 'ODataAdapter' -callerPSCmdlet $tmpcmdlet
            $moduleDir = join-path $TestDrive "v3Module"
            New-Item $moduleDir -ItemType Directory -ErrorAction SilentlyContinue | Out-Null
            $modulePath = Join-Path $moduleDir 'GeneratedModule.psd1'

            GenerateModuleManifest $metadata $modulePath @('GeneratedServiceActions.cdxml') $null 'Sample ProgressBar message'

            $fileContents = Get-Content $modulepath -Raw

            $rx = new-object System.Text.RegularExpressions.Regex('\bNestedModules = @\([^\)]*\)', ([System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline))
            $nestedModules = $rx.Match($fileContents).Value;

            $ns = new-object Xml.XmlNamespaceManager $xmlDoc.NameTable
            $ns.AddNamespace("m", $xmlDoc.Edmx.DataServices.m)
            $ns.AddNamespace("ns", $xmlDoc.Edmx.DataServices.Schema.xmlns)

            # expected NestedModules = EntitySets + Actions cdxml
            [int]$expectedActionModuleCount = @($xmlDoc.selectNodes('//ns:FunctionImport[not(@m:HttpMethod)]', $ns)).Count -gt 0
            $expectedNestedModulesCount = @($xmlDoc.selectNodes('//ns:EntityContainer/ns:EntitySet', $ns)).Count + $expectedActionModuleCount

            $rx2 = new-object System.Text.RegularExpressions.Regex('([\w]*\.cdxml)', ([System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline))
            $rx2.Matches($nestedModules).Count | Should Be $expectedNestedModulesCount
        }

        It "Verifies that BaseClassDefinitions can't be overwritten" {
            
            # at this point ODataUtilsHelper.ps1 was dot sourced and 'BaseClassDefinitions' variable is initialized with correct value
            # now try to overwrite variable
            Set-Variable -Name BaseClassDefinitions -Scope 'Global' -Value 'Uncompilable C# code' -ErrorAction SilentlyContinue
            
            # if above overwrite worked then Add-Type in the adapter will generate errors and fail the test
            . $scriptToDotSource
        }
    }

    Context "OData v4 validation test cases" {
    
        BeforeAll {
            $scriptToDotSource = Join-Path $ModuleBase 'Microsoft.PowerShell.ODataV4Adapter.ps1'
            . $scriptToDotSource

            $metadatav4XmlPath = Join-Path $script:TestSourceRoot "metadataV4.xml"
            $metadatav4Xml = Get-Content $metadatav4XmlPath
            [xml]$xmlDoc = $metadatav4Xml
            $ns = new-object Xml.XmlNamespaceManager $xmlDoc.NameTable
            $ns.AddNamespace("edmx", $xmlDoc.Edmx.edmx)
            $ns.AddNamespace("ns", $xmlDoc.Edmx.DataServices.Schema.xmlns)
        }

        It "Checks parsing metadata" {
            
            $MetadataSet = New-Object System.Collections.ArrayList
            $wp = $WarningPreference
            $WarningPreference = "SilentlyContinue"
            $result = ParseMetadata -MetadataXML $metadatav4Xml -MetadataSet $MetadataSet
            $WarningPreference = $wp
            $result.Namespace | Should Be "Microsoft.OData.SampleService.Models.TripPin"
            
            $result.EntitySets.Length | Should Be @($xmlDoc.selectNodes('//ns:EntitySet', $ns)).Count
            @($result.EntitySets | ?{$_.Name -eq 'People'}).Count | Should Be @($xmlDoc.selectNodes('//ns:EntitySet[@Name="People"]', $ns)).Count
            
            $result.EntityTypes.Length | Should Be @($xmlDoc.selectNodes('//ns:EntityType', $ns)).Count
            @($result.EntityTypes | ?{$_.Name -eq 'Person'}).Count | Should Be @($xmlDoc.selectNodes('//ns:EntityType[@Name="Person"]', $ns)).Count

            $result.ComplexTypes.Length | Should Be @($xmlDoc.selectNodes('//ns:ComplexType', $ns)).Count
            @($result.ComplexTypes | ?{$_.Name -eq 'Location'}).Count | Should Be @($xmlDoc.selectNodes('//ns:ComplexType[@Name="Location"]', $ns)).Count

            $result.EnumTypes.Length | Should Be @($xmlDoc.selectNodes('//ns:EnumType', $ns)).Count
            @($result.EnumTypes | ?{$_.Name -eq 'PersonGender'}).Count | Should Be @($xmlDoc.selectNodes('//ns:EnumType[@Name="PersonGender"]', $ns)).Count

            $result.SingletonTypes.Length | Should Be @($xmlDoc.selectNodes('//ns:Singleton', $ns)).Count
            @($result.SingletonTypes | ?{$_.Name -eq 'Me'}).Count | Should Be @($xmlDoc.selectNodes('//ns:Singleton[@Name="Me"]', $ns)).Count

            $result.Actions.Length | Should Be @($xmlDoc.selectNodes('//ns:Action', $ns)).Count
            $result.Functions.Length | Should Be @($xmlDoc.selectNodes('//ns:Function', $ns)).Count
        }

        It "Verify normalization" {

            #Verifies that NormalizeNamespace normalizes namespace name as expected.
            $normalizedNamespaces = @{}
            NormalizeNamespace 'Microsoft.OData.SampleService.Models.TripPin.1.0.0' 'SomeUri' $normalizedNamespaces $false
            $normalizedNamespaces.Count | Should Be 1
            GetNamespace 'Microsoft.OData.SampleService.Models.TripPin.1.0.0' $normalizedNamespaces $false | Should Be "Microsoft_OData_SampleService_Models_TripPin_1_0_0"

            #Verifies that NormalizeNamespace normalizes alias name as expected.
            $normalizedNamespaces = @{}
            NormalizeNamespace 'TripPin.1.0.0' 'SomeUri' $normalizedNamespaces $false
            $normalizedNamespaces.Count | Should Be 1
            GetNamespace 'TripPin.1.0.0' $normalizedNamespaces $false | Should Be "TripPin_1_0_0"

            #Verifies that NormalizeNamespace normalizes namespace name as expected.
            $normalizedNamespaces = @{}
            NormalizeNamespace 'Microsoft.OData.SampleService.Models.TripPin' 'SomeUri' $normalizedNamespaces $true
            $normalizedNamespaces.Count | Should Be 1
            GetNamespace 'Microsoft.OData.SampleService.Models.TripPin' $normalizedNamespaces $false | Should Be "Microsoft.OData.SampleService.Models.TripPinNs"

            #Verifies that NormalizeNamespace normalizes alias name as expected.
            $normalizedNamespaces = @{}
            NormalizeNamespace 'TripPin' 'SomeUri' $normalizedNamespaces $true
            $normalizedNamespaces.Count | Should Be 1
            GetNamespace 'TripPin' $normalizedNamespaces $false | Should Be "TripPinNs"

            #Verifies that IsNamespaceNormalizationNeeded returns true when namespace contains combination of dots and numbers.
            $normalizedNamespaces = @{}
            NormalizeNamespace 'Microsoft.OData.SampleService.Models.TripPin.1.0.0' 'SomeUri' $normalizedNamespaces $false
            $normalizedNamespaces.Count | Should Be 1

            #Verifies that IsNamespaceNormalizationNeeded returns false when namespace is a combination of Namespace and TypeName and namespace name does not require normalization.
            $normalizedNamespaces = @{}
            NormalizeNamespace 'Microsoft.OData.SampleService.Models.TripPin' 'SomeUri' $normalizedNamespaces $false
            GetNamespace 'Microsoft.OData.SampleService.Models.TripPin.Photo' $normalizedNamespaces $true | Should Be "Microsoft.OData.SampleService.Models.TripPin.Photo"

            #Verifies that IsNamespaceNormalizationNeeded returns true when namespace contains combination of dots and numbers.
            $normalizedNamespaces = @{}
            NormalizeNamespace 'Microsoft.OData.SampleService.Models.TripPin' 'SomeUri' $normalizedNamespaces $false
            $normalizedNamespaces.Count | Should Be 0
        }

        It "Verifies that generated module has correct contents" {
            
            $GlobalMetadata = New-Object System.Collections.ArrayList
            $metadata = ParseMetadata -MetadataXML $metadatav4Xml -MetadataSet $GlobalMetadata
            $GlobalMetadata.Add($metadata)
            $normalizedNamespaces = @{}
            $entitySet = $metadata.EntitySets[0]
            [string]$generatedModuleName = $entitySet.Type.Name
            $moduleDir = join-path $TestDrive "v4Module"
            New-Item $moduleDir -ItemType Directory | Out-Null

            SaveCDXML $entitySet $metadata $GlobalMetadata 'http://fakeuri/Service.svc' $moduleDir 'Post' 'Patch' 'ODataV4Adapter' -UriResourcePathKeyFormat 'EmbeddedKey' -normalizedNamespaces $normalizedNamespaces

            $modulepath = join-path $moduleDir $generatedModuleName
            $modulepath += ".cdxml"
            [xml]$doc = Get-Content $modulepath -Raw
            $ns = new-object Xml.XmlNamespaceManager $doc.NameTable
            $ns.AddNamespace("ns", $doc.PowerShellMetadata.xmlns)

            $queryableProperties = $doc.GetElementsByTagName("QueryableProperties")
            $queryableProperties.Count | Should Be @($doc.SelectNodes('//ns:QueryableProperties', $ns)).Count
            $queryableProperties[0].ChildNodes.Count | Should Be @($doc.SelectNodes('//ns:QueryableProperties/*', $ns)).Count
            $doc.GetElementsByTagName("GetCmdlet").Count | Should Be @($doc.SelectNodes('//ns:GetCmdlet', $ns)).Count
            $staticCmdlets = $doc.GetElementsByTagName("StaticCmdlets")
            $staticCmdlets.Count | Should Be @($doc.SelectNodes('//ns:StaticCmdlets', $ns)).Count
            $staticCmdlets[0].ChildNodes.Count | Should Be @($doc.SelectNodes('//ns:StaticCmdlets/*', $ns)).Count
            $doc.GetElementsByTagName("Cmdlet").Count | Should Be @($doc.SelectNodes('//ns:Cmdlet', $ns)).Count
            $doc.GetElementsByTagName("Method").Count | Should Be @($doc.SelectNodes('//ns:Method', $ns)).Count
        }
        
        It "Verifies that generated module manifest has correct amount of nested modules" {
            
            $GlobalMetadata = New-Object System.Collections.ArrayList
            $metadata = ParseMetadata -MetadataXML $metadatav4Xml -MetadataSet $GlobalMetadata
            $GlobalMetadata.Add($metadata)


            $moduleDir = join-path $TestDrive "v4Module"
            New-Item $moduleDir -ItemType Directory -ErrorAction SilentlyContinue | Out-Null
            $modulePath = Join-Path $moduleDir 'GeneratedModule.psd1'

            GenerateModuleManifest $GlobalMetadata $modulePath @('GeneratedServiceActions.cdxml') $null 'Sample ProgressBar message'

            $fileContents = Get-Content $modulepath -Raw

            [xml]$xmlDoc = $metadatav4Xml
            $ns = new-object Xml.XmlNamespaceManager $xmlDoc.NameTable
            $ns.AddNamespace("edmx", $xmlDoc.Edmx.edmx)
            $ns.AddNamespace("ns", $xmlDoc.Edmx.DataServices.Schema.xmlns)

            # expected NestedModules = EntitySets + Singletons + Actions cdxml
            [int]$expectedActionModuleCount = @($xmlDoc.selectNodes('//ns:FunctionImport', $ns)).Count -gt 0
            [int]$expectedSingletonModuleCount = @($xmlDoc.selectNodes('//ns:Singleton', $ns)).Count
            $expectedNestedModulesCount = @($xmlDoc.selectNodes('//ns:EntityContainer/ns:EntitySet', $ns)).Count + $expectedActionModuleCount + $expectedSingletonModuleCount

            $rx = new-object System.Text.RegularExpressions.Regex('\bNestedModules = @\([^\)]*\)', ([System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline))
            $nestedModules = $rx.Match($fileContents).Value;

            $rx2 = new-object System.Text.RegularExpressions.Regex('([\w]*\.cdxml)', ([System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline))
            $rx2.Matches($nestedModules).Count | Should Be $expectedNestedModulesCount
        }

        It "Verifies that BaseClassDefinitions can't be overwritten" {
            
            # at this point ODataUtilsHelper.ps1 was dot sourced and 'BaseClassDefinitions' variable is initialized with correct value
            # now try to overwrite variable
            Set-Variable -Name BaseClassDefinitions -Scope 'Global' -Value 'Uncompilable C# code' -ErrorAction SilentlyContinue
            
            # if above overwrite worked then Add-Type in the adapter will generate errors and fail the test
            . $scriptToDotSource
        }
    }

    Context "Redfish validation test cases" {
    
        BeforeAll {
            $scriptToDotSource = Join-Path $ModuleBase 'Microsoft.PowerShell.RedfishAdapter.ps1'
            . $scriptToDotSource

            $metaFilesRoot = Join-Path $script:TestSourceRoot 'RedfishData'
            $metaFilePaths = Get-ChildItem $metaFilesRoot -Filter '*.xml'

            $metaXmls= @()

            $SchemaCount = 0;
            $EntityTypesCount = 0;
            $ComplexTypesCount = 0;
            $EnumTypesCount = 0;
            $SingletonTypesCount = 0;
            $ActionsCount = 0;
            $NavigationPropertiesCount = 0;
            
            foreach($metaFile in $metaFilePaths)
            {
                $metaXml = Get-Content $metaFile.FullName -Raw
                $metaXmls += $metaXml

                [xml]$xmlDoc = $metaXml
                
                foreach($s in $xmlDoc.Edmx.DataServices.Schema)
                {
                    $ns = new-object Xml.XmlNamespaceManager $xmlDoc.NameTable
                    $ns.AddNamespace("ns", "http://docs.oasis-open.org/odata/ns/edm")    

                    $SchemaCount += 1
                    $EntityTypesCount += @($s.selectNodes('ns:EntityType', $ns)).Count
                    $ComplexTypesCount += @($s.selectNodes('ns:ComplexType', $ns)).Count
                    $EnumTypesCount += @($s.selectNodes('ns:EnumType', $ns)).Count
                    $SingletonTypesCount += @($s.selectNodes('ns:EntityContainer/ns:Singleton', $ns)).Count
                    $ActionsCount += @($s.selectNodes('ns:Action', $ns)).Count
                    $NavigationPropertiesCount += @($s.selectNodes('ns:EntityType/ns:NavigationProperty', $ns)).Count
                }
            }
        }

        It "Checks parsing metadata" {
            
            # based on Redfish Schema DSP8010 / 2016.1 / 31 May 2016

            try { ExportODataEndpointProxy } catch {} # calling this here just to initialize module variables

            foreach($metaXml in $metaXmls)
            {
                ParseMetadata -MetadataXML $metaXml -ODataVersion '4.0' -MetadataUri 'http://fakeuri/redfish/v1/$metadata' -Uri 'http://fakeuri/redfish/v1'
            }
            
            @($GlobalMetadata | ?{if ($_) {$_}}).Count | Should Be $SchemaCount
            @($GlobalMetadata.EntityTypes | ?{if ($_) {$_}}).Count | Should Be $EntityTypesCount
            @($GlobalMetadata.ComplexTypes | ?{if ($_) {$_}}).Count | Should Be $ComplexTypesCount
            @($GlobalMetadata.EnumTypes | ?{if ($_) {$_}}).Count | Should Be $EnumTypesCount
            @($GlobalMetadata.SingletonTypes | ?{if ($_) {$_}}).Count | Should Be $SingletonTypesCount
            @($GlobalMetadata.Actions | ?{if ($_) {$_}}).Count | Should Be $ActionsCount
            @($GlobalMetadata.EntityTypes.NavigationProperties | ?{if ($_) {$_}}).Count | Should Be $NavigationPropertiesCount
        }

        It "Verifies that generated module has correct contents" {
            
            try { ExportODataEndpointProxy } catch {} # calling this here just to initialize module variables

            foreach($metaXml in $metaXmls)
            {
                ParseMetadata -MetadataXML $metaXml -ODataVersion '4.0' -MetadataUri 'http://fakeuri/redfish/v1/$metadata' -Uri 'http://fakeuri/redfish/v1'
            }

            $moduleDir = join-path $TestDrive "RedfishModule"
            New-Item $moduleDir -ItemType Directory | Out-Null

            $ODataEndpointProxyParameters = [ODataUtils.ODataEndpointProxyParameters] @{
                "MetadataUri" = 'http://fakeuri/redfish/v1/$metadata';
                "Uri" = 'http://fakeuri/redfish/v1';
                "OutputModule" = $moduleDir;
                "Force" = $true;
            }
            
            GenerateClientSideProxyModule $GlobalMetadata $ODataEndpointProxyParameters $moduleDir 'Post' 'Patch' 'ODataV4Adapter' -progressBarStatus 'generating module'

            # check generated files in module directory
            $nestedModulesCount = @(dir $moduleDir -Filter '*.cdxml').Count
            $psd1 = dir $moduleDir -Filter '*.psd1'
            @($psd1).Count | Should Be 1

            # basic check for generated psd1
            $fileContents = Get-Content $psd1.FullName -Raw
            $rx = new-object System.Text.RegularExpressions.Regex('\bNestedModules = @\([^\)]*\)', ([System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline))
            $nestedModules = $rx.Match($fileContents).Value;
            $rx2 = new-object System.Text.RegularExpressions.Regex('([\w]*\.cdxml)', ([System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline))
            $rx2.Matches($nestedModules).Count | Should Be $nestedModulesCount

            # basic check for ServiceRoot cdxml
            @(dir $moduleDir -Filter 'ServiceRoot.cdxml').Count | Should Be 1
            
            # basic check for other sample cdxml
            $modulepath = Join-Path $moduleDir 'ComputerSystem.cdxml'
            [xml]$doc = Get-Content $modulepath -Raw
            $doc.GetElementsByTagName("GetCmdlet").Count | Should Be 1
        }

        It "Verifies that BaseClassDefinitions can't be overwritten" {
            
            # at this point ODataUtilsHelper.ps1 was dot sourced and 'BaseClassDefinitions' variable is initialized with correct value
            # now try to overwrite variable
            Set-Variable -Name BaseClassDefinitions -Scope 'Global' -Value 'Uncompilable C# code' -ErrorAction SilentlyContinue
            
            # if above overwrite worked then Add-Type in the adapter will generate errors and fail the test
            . $scriptToDotSource
        }
    }
}