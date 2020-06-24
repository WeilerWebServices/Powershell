## Function to import .pfx certificate on current system.
function Install-Certificate
{
    Param(   
        [Parameter(Mandatory=$true)][ValidateNotNullorEmpty()][string] $OsVersion,     
        [Parameter(Mandatory=$true)][ValidateNotNullorEmpty()][string] $CertificateFilePath,
        [Parameter(Mandatory=$true)][ValidateNotNullorEmpty()][SecureString] $CertificatePassword
    )

    try
    {
        ## Check for Os version and call the powershell command to import pfx accordingly
        ## 6.1 -> Windows server 2008 R2
        if($OsVersion.StartsWith("6.1"))
        {
            $pfx = new-object System.Security.Cryptography.X509Certificates.X509Certificate2
            $pfx.import($CertificateFilePath,$CertificatePassword,“Exportable,PersistKeySet”)
            $CertStore = new-object System.Security.Cryptography.X509Certificates.X509Store(“MY”,“localmachine”)
            $CertStore.open(“MaxAllowed”)
            $CertStore.add($pfx)
            $CertStore.close()
        }
        else
        {
            $store = "cert:\localmachine\MY"
            Import-PfxCertificate -Filepath $CertificateFilePath -CertStoreLocation $store -Password $CertificatePassword -ErrorAction Stop | out-null
        }    
    }
    catch
    {
        Write-Error "Exception in importing certificate!" 
        throw
    }
}

## Function to check if sil is installed on Collector Server
function CheckSILInstalled
{
    ## Get Powershell Version on Collector Server
    if($CollectorCredentialRequired)
    {
        $psVersion = Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential {$PSVersionTable.PSVersion.Major} -ErrorAction stop
    }
    else
    {
        $psVersion = Invoke-Command -ComputerName $SilCollectorServer {$PSVersionTable.PSVersion.Major} -ErrorAction stop
    }

    if($psVersion -lt 4)
    {
        Write-Error "Software Inventory Logging is not installed on $SilCollectorServer."
        return $false
    }

    if($psVersion -eq 5)
    {
        return $true
    }

    ## Get OS information on collector server
    if($CollectorCredentialRequired)
    {
        $osInfo = Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential {Get-WmiObject -class Win32_OperatingSystem} -ErrorAction stop
    }
    else
    {
        $osInfo = Invoke-Command -ComputerName $SilCollectorServer {Get-WmiObject -class Win32_OperatingSystem} -ErrorAction stop
    }
    ## Check the OS version on collector machine and checks for updates accordingly
    ## 6.1 -> Windows server 2008 R2
    ## 6.2 -> Windows server 2012
    ## 6.3 -> Windows Server 2012 R2
    if($osInfo.Version.StartsWith("6.1"))
    {
        try 
        {
            if($CollectorCredentialRequired)
            {
                $update = Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential {Get-HotFix -ID KB3109118} -ErrorAction stop

            }
            else
            {
                $update = Invoke-Command -ComputerName $SilCollectorServer {Get-HotFix -ID KB3109118} -ErrorAction stop
            }


            return $true
        }
        catch 
        {
            Write-Error "Software Inventory Logging cannot be configured. The required SIL update KB3109118 is not found on $SilCollectorServer."
            throw
        }
    }
    elseif($osInfo.Version.StartsWith("6.2"))
    {
        try
        {
            if($CollectorCredentialRequired)
            {
                $update = Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential {Get-HotFix -ID KB3119938} -ErrorAction stop
            }
            else
            {
                $update = Invoke-Command -ComputerName $SilCollectorServer {Get-HotFix -ID KB3119938} -ErrorAction stop
            }
            return $true
        }
        catch
        {
            Write-Error "Software Inventory Logging cannot be configured. The required SIL update KB3119938 is not found on $SilCollectorServer."
            throw
        }
    
    }
    elseif($osInfo.Version.StartsWith("6.3"))
    {
        try
        {
            if($CollectorCredentialRequired)
            {
                [System.Array]$update = Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential {Get-HotFix -ID KB3060681, KB3000850} -ErrorAction stop
            }
            else
            {
                [System.Array]$update = Invoke-Command -ComputerName $SilCollectorServer {Get-HotFix -ID KB3060681, KB3000850} -ErrorAction stop
            }

            if($update.Count -eq 1)
            {
                if(-not($update.HotFixID.Contains("KB3060681")))
                {
                    Write-Error "Software Inventory Logging cannot be configured. The required SIL update (KB3060681) is not found on $SilCollectorServer."
                    return $false
                }
                else
                {
                    Write-Error "Software Inventory Logging cannot be configured. The required SIL update (KB3000850) is not found on $SilCollectorServer."
                    return $false
                }
            }

            return $true
        }
        catch
        {
            Write-Error "Software Inventory Logging cannot be configured. The required SIL updates (KB3060681 & KB3000850) are not found on $SilCollectorServer."
            throw
        }
    
    }
}

<#
.Synopsis
  Enable and configure Software Inventory Logging on remote computers.

.Description
  Enable and configure Software Inventory Logging on remote computers 
   and installs required client certificate.

.NOTES

  Author: Microsoft Corporation
  Date  : 2015/12/28
  Vers  : 1.0
  
  Updates:

.PARAMETER SilCollectorServer
                Specifies a remote server to be enabled and configured for Software Inventory Logging.
                
.PARAMETER SilCollectorServerCredential
                Specifies the credentials that allow this script to connect to the remote SIL Collector server. 
                To obtain a PSCredential object, use the ‘Get-Credential’ cmdlet. 
                For more information, type Get-Help Get-Credential.

.PARAMETER SilAggregatorServer
                Specifies the SIL Aggregator server. 
                This server must have Software Inventory Logging Aggregator 1.0 installed.

.PARAMETER SilAggregatorServerCredential
                Specifies the credentials that allow this script to connect to the remote SIL Aggregator server. 
                To obtain a PSCredential object, use the ‘Get-Credential’ cmdlet. 
                For more information, type Get-Help Get-Credential.

.PARAMETER CertificateFilePath
                Specifies the path for the PFX file.
                
.PARAMETER CertificatePassword
                Specifies the password for the imported PFX file in the form of a secure string.
                
.LINK
                https://technet.microsoft.com/en-us/library/dn268301.aspx
                
.EXAMPLE
    $CollectorCreds = Get-Credential
    $AggregatorCreds = Get-Credential
    $CertPswd = Read-Host -AsSecureString
    
    .\Enable-SilCollector -SilCollectorServer Contoso -SilCollectorServerCredential $CollectorCreds -SilAggregatorServer AggregatorContoso -SilAggregatorServerCredential $AggregatorCreds -CertificateFilePath "C:\Users\User1\Desktop\Contoso.pfx -CertificatePassword $CertPswd

    This command configures Software Inventory Logging in a remote computer.

.EXAMPLE
    $Servers = "Contoso1", "Contoso2", "Contoso3", "Contoso4", "Contoso5"
    $AggregatorServer = "SILAggregator"
    $AggregatorCreds = Get-Credential
    $CertPswd = Read-Host -AsSecureString

    for($i=0; $i -lt $Servers.Length; $i++)
    {
        try
        {
            Enable-SilCollector -SilCollectorServer $Servers[$i] -SilAggregatorServer $AggregatorServer -SilAggregatorServerCredential $AggregatorCreds -CertificateFilePath "C:\Users\User1\Desktop\Contoso.pfx" -CertificatePassword $CertPswd
        }
        catch
        {
            Write-Host $_.Exception -ForegroundColor Red
            Continue
        }
    }

    This command configures Software Inventory Logging in multiple computers. The credentials to connect to each remote servers will be requested at run time.

#>
function Enable-SILCollector
{
Param
(
    [Parameter(Mandatory=$true, HelpMessage="Specifies a remote server to be enabled and configured for Software Inventory Logging.")]
    [ValidateNotNullOrEmpty()][string] $SilCollectorServer,
    [Parameter(Mandatory=$false, HelpMessage="Specifies the credentials that allow this script to connect to the remote SIL Collector server.")]
    [ValidateNotNull()][PSCredential] $SilCollectorServerCredential,
    [Parameter(Mandatory=$true, HelpMessage="Specifies the SIL Aggregator server. This server must have Software Inventory Logging Aggregator 1.0 installed.")]
    [ValidateNotNullOrEmpty()][string] $SilAggregatorServer,
    [Parameter(Mandatory=$false, HelpMessage="Specifies the credentials that allow this script to connect to the remote SIL Aggregator server.")]
    [ValidateNotNull()][PSCredential] $SilAggregatorServerCredential,
    [Parameter(Mandatory=$true, HelpMessage="Specifies the path for the PFX file.")]
    [ValidateScript({
            if (-not ($_ -match ('\.pfx$')))
            {
                throw "The certificate must be of '.PFX' format."
            }
            Return $true
        })][string] $CertificateFilePath,
    [Parameter(Mandatory=$true, HelpMessage="Specifies the password for the imported PFX file in the form of a secure string.")]
    [SecureString] $CertificatePassword
)
    try
    {
        ## Check if Script is not executing from a non admin PS prompt.
        if(-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator"))
        {
            throw "Error!!! Please login using admin credentials."
        }

        ## Validate Certificate File Path.
        if( -not (Test-Path $CertificateFilePath))
        {
            throw "Error!!! $CertificateFilePath is invalid."
        }
    
        ## Validate the Certificate Password.
        try
        {
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
            $cert.Import($CertificateFilePath, $CertificatePassword, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)

            ## Check if certificate thumbprint already exists or not.
            ## This is to make sure that if multiple computers are being configured in a loop using same certificate,
            ## then script should not make redundant calls to Aggregator server to register same certificate thumbprint.
            if($global:CertificateThumbprint)
            {
                if($cert.Thumbprint -ne $global:CertificateThumbprint)
                {
                    ## If both certificate thumbprint values are not equal, set the new certificate thumbprint.
                    $global:CertificateThumbprint = $cert.Thumbprint
                
                    ## Set flag to register certifiacate thumbprint with Aggregator.
                    $EnableSilAggregator = $true
                }
                else
                {
                    ## Set flag for not to register certifiacate thumbprint with Aggregator.
                    $EnableSilAggregator = $false
                }
            }
            else
            {
                ## If certificate thumbprint not exists, set certificate thumbprint value.
                $global:CertificateThumbprint = $cert.Thumbprint
                $EnableSilAggregator = $true
            }
        }
        catch 
        {
            Write-Error "Certificate Password is Incorrect."
            throw
        }

        ## local variables
        $AggregatorAccessible = $false
        $AggregatorCredentialRequired = $false
        $AggregatorInSameDomain = $false

        $CollectorAccessible = $false
        $CollectorCredentialRequired = $false
        $CollectorInSameDomain = $false

        ## Test PS connectivity with Collector and Aggregator servers using different configurations.
        ## Test 1 - w/o Credentials for Collector
        try
        {
            $remoteHostName = Invoke-Command -ComputerName $SilCollectorServer -ScriptBlock {$env:computername} -ErrorAction stop
            $CollectorAccessible = $true
            $CollectorCredentialRequired = $false
            $CollectorInSameDomain = $true
        }
        catch
        {
            ## If access denied, then try with password or after trusted host list update.
            if ($_.Exception.ErrorCode -eq 5)
            {
                $CollectorInSameDomain = $true
            }
        }

        ## Test 2 - w/ Credentials for Collector
        if (-not $CollectorAccessible -and $CollectorInSameDomain)
        {
            try
            {
                if(-not($SilCollectorServerCredential))
                {
                    $SilCollectorServerCredential = $host.ui.PromptForCredential("Enter Credential", "Please enter a user credential to connect to computer '$SilCollectorServer'.", "", $SilCollectorServer)
                }
            
                $remoteHostName = Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential -ScriptBlock {$env:computername} -ErrorAction stop
                $CollectorAccessible = $true
                $CollectorCredentialRequired = $true
            }
            catch
            {
        
                ## If still access denied, then return, else try after trusted host list update.
                if ($_.Exception.ErrorCode -eq 5)
                {
                    Write-Error "Error in connecting to remote server $SilCollectorServer."
                    Write-Error "$($SilCollectorServerCredential.UserName) does not have required access, Or Password is incorrect , or PowerShell remoting is not enabled on Collector server."
                    throw
                }
            }
        }

        ## Test 1 - w/o Credentials for Aggregator
        try
        {
            $AggregatorHostName = Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock {$env:computername} -ErrorAction stop
            $AggregatorAccessible = $true
            $AggregatorCredentialRequired = $false
            $AggregatorInSameDomain = $true
        }
        catch
        {
            ## If access denied, then try with password or after trusted host list update.
            if ($_.Exception.ErrorCode -eq 5)
            {
                $AggregatorInSameDomain = $true
            }
        }

        ## Test 2 - w/ Credentials for Aggregator

        if (-not $AggregatorAccessible -and $AggregatorInSameDomain)
        {
            try
            {
                if(-not $SilAggregatorServerCredential)
                {
                    $SilAggregatorServerCredential = $host.ui.PromptForCredential("Enter Credential", "Please enter a user credential to connect to the SIL Aggregator Server '$SilAggregatorServer'.", "", $SilAggregatorServer)
                }
            
                $AggregatorHostName = Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock {$env:computername} -ErrorAction stop
                $AggregatorAccessible = $true
                $AggregatorCredentialRequired = $true
            }
            catch
            {
        
                ## If still access denied, then return, else try after trusted host list update.
                if ($_.Exception.ErrorCode -eq 5)
                {
                    Write-Error "Error in connecting to Aggregator server $SilAggregatorServer."
                    Write-Error "$($SilAggregatorServerCredential.UserName) does not have required access, Or Password is incorrect , or PowerShell remoting is not enabled on Collector server."
                    throw
                }
            }
        }

        ## Test 3 - Different domain / Workgroup, test by modifying trusted hosts settings.
        if (-not $AggregatorAccessible -or -not $CollectorAccessible)
        {
            ## Get the current value of trusted host list
            $trustedHostsModified = $false
            [string]$curTrustedHosts = (get-item wsman:\localhost\Client\TrustedHosts -ErrorAction Stop).value

            ## Check if current trusted host list is not empty.
            if(-not $curTrustedHosts)
            {
                if(-not $AggregatorAccessible -and -not $CollectorAccessible)
                {
                    $newValue = [string]::Format("{0}, {1}", $SilCollectorServer, $SilAggregatorServer)
                    $trustedHostsModified = $true
                }
                elseif (-not $CollectorAccessible)
                {
                    $newValue = $SilCollectorServer
                    $trustedHostsModified = $true
                }
                elseif (-not $AggregatorAccessible)
                {
                    $newValue = $SilAggregatorServer
                    $trustedHostsModified = $true
                }
            }
            else
            {
                if((-not($curTrustedHosts.ToLower().Contains($SilCollectorServer.ToLower()))) -and (-not($curTrustedHosts.TOLower().Contains($SilAggregatorServer.ToLower()))) -and (-not $AggregatorAccessible) -and (-not $CollectorAccessible))
                {
                    $newValue = [string]::Format("{0}, {1}, {2}", $curTrustedHosts, $SilCollectorServer, $SilAggregatorServer)
                    $trustedHostsModified = $true
                }
                elseif (-not($curTrustedHosts.ToLower().Contains($SilCollectorServer.ToLower())) -and (-not $CollectorAccessible))
                {
                    $newValue = [string]::Format("{0}, {1}", $curTrustedHosts, $SilCollectorServer)
                    $trustedHostsModified = $true
                }
                elseif (-not($curTrustedHosts.ToLower().Contains($SilAggregatorServer.ToLower())) -and (-not($AggregatorAccessible)))
                {
                    $newValue = [string]::Format("{0}, {1}", $curTrustedHosts, $SilAggregatorServer)
                    $trustedHostsModified = $true
                }
            }

            ## Update trusted hosts list.
            if($trustedHostsModified)
            {
                set-item wsman:\localhost\Client\TrustedHosts -value $newValue -Force
            }

            ## Test SIL Collector Connection    
            try
            {
                if(-not($SilCollectorServerCredential))
                {
                    $SilCollectorServerCredential = $host.ui.PromptForCredential("Enter Credential", "Please enter a user credential to connect to computer '$SilCollectorServer'.", "", $SilCollectorServer)
                }

                $remoteHostName = Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential -ScriptBlock {$env:computername} -ErrorAction stop
                $CollectorAccessible = $true
                $CollectorCredentialRequired = $true
                $CollectorInSameDomain = $false
            }
            catch
            {
                ## If still access denied, then return, else try after trusted host list update.
                if ($_.Exception.ErrorCode -eq 5)
                {
                    Write-Error "Error in connecting to remote server $SilCollectorServer."
                    Write-Error "$($SilCollectorServerCredential.UserName) does not have required access, Or Password is incorrect , or PowerShell remoting is not enabled on Collector server."
                }
                throw
            }

            ## Test SIL Aggregator Connection    
            try
            {
                if(-not $SilAggregatorServerCredential)
                {
                    $SilAggregatorServerCredential = $host.ui.PromptForCredential("Enter Credential", "Please enter a user credential to connect to the SIL Aggregator Server '$SilAggregatorServer'.", "", $SilAggregatorServer)
                }

                $AggregatorHostName = Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock {$env:computername} -ErrorAction stop
                $AggregatorAccessible = $true
                $AggregatorCredentialRequired = $true
                $AggregatorInSameDomain = $false
            }
            catch
            {
                ## If still access denied, then return, else try after trusted host list update.
                if ($_.Exception.ErrorCode -eq 5)
                {
                    Write-Error "Error in connecting to Aggregator server $SilAggregatorServer."
                    Write-Error "$($SilAggregatorServerCredential.UserName) does not have required access, Or Password is incorrect , or PowerShell remoting is not enabled on Collector server."
                }
                throw
            }
        }

        ## Check if SIL Aggregator installed on aggregator server machine.
        try
        {
            ## If target Uri value doesn't exists, get the target uri value from SIL aggregator server.
            if(-not($global:TargetUri))
            {
                if($AggregatorCredentialRequired)
                {
                    $AggregatorInfo = Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential {Get-SilAggregator} -ErrorAction stop
                }
                else
                {
                    $AggregatorInfo = Invoke-Command -ComputerName $SilAggregatorServer {Get-SilAggregator} -ErrorAction stop
                }
                $global:TargetUri = $AggregatorInfo.TargetURI
            }
        }
        catch
        {
            Write-Error "Error!!! Software Inventory Logging Aggregator 1.0 is not installed on $SilAggregatorServer ."
            throw
        }

        ## variable to track if SIL is configured.
        $SILEnabled = $false
                                
        ## Check updates on SIL collector server.
        try
        {
            if (-not (CheckSILInstalled))
            {
                throw
            }
    
        }
        catch
        {
            throw
        }           
    
        ## Copy certificate to SIL collector server                            
        
        ## extract the certificate name from Certificate File path
        $cert = [IO.Path]::GetFileName($CertificateFilePath)

        ## Get the fixed drive name on remote machine
        if($CollectorCredentialRequired)
        {
            $drive = Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential -ScriptBlock {Get-WMIObject -Class Win32_Volume} -ErrorAction Stop
        }
        else
        {
            $drive = Invoke-Command -ComputerName $SilCollectorServer -ScriptBlock {Get-WMIObject -Class Win32_Volume} -ErrorAction Stop
        }

        foreach ($d in $drive)
        {
           if((($d.DriveType.ToString().ToLower() -eq "fixed") -or ($d.DriveType -eq 3)) -and ($d.DriveLetter)) 
           {
               $availableDisk = $d.DriveLetter
               break
           }
        }

        if($availableDisk)
        {
            ## copy certificate on remote collector server
            $contents = [IO.File]::ReadAllBytes($CertificateFilePath)
            $remoteCert = $availableDisk + "\" + $cert

            if($CollectorCredentialRequired)
            {
                Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential -ScriptBlock { [IO.File]::WriteAllBytes( $using:remoteCert, $using:contents ) }
            }
            else
            {
                Invoke-Command -ComputerName $SilCollectorServer -ScriptBlock { [IO.File]::WriteAllBytes( $using:remoteCert, $using:contents ) }
            }
        }
        else
        {
            Write-Error "No Fixed Drive on $SilCollectorServer is available to copy certificate."
            return
        }

        ## Import the certificate on SIL collector server

        ## Import in current User personal store
        $certPath = $availableDisk + "\" + $cert
        if($CollectorCredentialRequired)
        {
            $osInfo = Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential {Get-WmiObject -class Win32_OperatingSystem} -ErrorAction stop
            Invoke-Command  -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential -ScriptBlock ${function:Install-Certificate} -ArgumentList $osInfo.Version, $certPath, $CertificatePassword -ErrorAction Stop
        }
        else
        {
            $osInfo = Invoke-Command -ComputerName $SilCollectorServer {Get-WmiObject -class Win32_OperatingSystem} -ErrorAction stop
            Invoke-Command  -ComputerName $SilCollectorServer -ScriptBlock ${function:Install-Certificate} -ArgumentList $osInfo.Version, $certPath, $CertificatePassword -ErrorAction Stop
        }
    
        ## Delete certificate copied on sil collector server
        if($CollectorCredentialRequired)
        {
            Invoke-Command  -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential -ScriptBlock {Remove-Item $using:remoteCert} -ErrorAction Stop
        }
        else
        {
            Invoke-Command  -ComputerName $SilCollectorServer -ScriptBlock {Remove-Item $using:remoteCert} -ErrorAction Stop
        }
     
        ## Script Block to execute Set-SilLogging command on SIL collector server                           
        $sb = {
                    param($tu,$ct)
                    $SilArguments = @{
                        TargetUri = $tu
                        CertificateThumbprint = $ct
                }                              
                Set-SilLogging @SilArguments                                
                Start-SilLogging
              }
   
        ## Run Set-SilLogging Command on Remote SIL collector server
        if($CollectorCredentialRequired)
        {
            Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential -ScriptBlock $sb -ArgumentList $global:TargetUri, $global:CertificateThumbprint -ErrorAction Stop
        }
        else
        {
            Invoke-Command -ComputerName $SilCollectorServer -ScriptBlock $sb -ArgumentList $global:TargetUri, $global:CertificateThumbprint -ErrorAction Stop
        }
        $SILEnabled = $true
    
        ## Script Block to execute Set-SilAggregator command on SIL Aggregator server
        $sbAggregator = {
                param($tu)
                Set-SilAggregator –AddCertificateThumbprint $tu -Force
              }
        try
        {
            ## Run Set-SilAggregator command on SIL Aggregator server 
            if ($SILEnabled -and ($EnableSilAggregator -or (-not($global:AggregatorException))))
            {
                if($AggregatorCredentialRequired)
                {
                    Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock $sbAggregator -ArgumentList $global:CertificateThumbprint -ErrorAction Stop 2>&1 | Out-Null
                }
                else
                {
                    Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock $sbAggregator -ArgumentList $global:CertificateThumbprint -ErrorAction Stop 2>&1 | Out-Null
                }

                ## variable for exception in Set-SilAggregator command 
                $global:AggregatorException = $true
            }
        }
        catch
        {
            $global:AggregatorException = $false
            throw
        }

        try
        {
            ## Run Publish-SilData Command on remote SIL collector server
            if ($SILEnabled)
            {
                if($CollectorCredentialRequired)
                {
                    Invoke-Command -ComputerName $SilCollectorServer -Credential $SilCollectorServerCredential -ScriptBlock {Publish-SilData} -ErrorAction Stop
                }
                else
                {
                    Invoke-Command -ComputerName $SilCollectorServer -ScriptBlock {Publish-SilData} -ErrorAction Stop
                }
            }
        }
        catch
        {
            Write-Warning $_.Exception.Message 
        }
    }
    catch
    {
        throw $_.Exception
    }

    ## Revert back the trusted host settings 
    finally
    {
        if($trustedHostsModified)
        {
            set-item wsman:\localhost\Client\TrustedHosts -value "$curTrustedHosts" -Force
        }
    }
    
    if($SILEnabled)
    {
        ## Display Success Message
        Write-Host "Software Inventory Logging configured successfully on Server: $SilCollectorServer."
    }
}

<#
.Synopsis
  Enable and configure Software Inventory Logging (SIL) in a Virtual Hard Disk.

.Description
  Enable and configure Software Inventory Logging (SIL) in a Virtual Hard Disk using SIL Registry key settings in VHD.
  It also installs the client certificate on every Virtual Machine using Windows RunOnce registry.


.INPUTS

.OUTPUTS

.NOTES

  Author: Microsoft
  Date  : 2016/01/12
  Vers  : 1.0
  
  Updates:


.PARAMETER VirtualHardDiskPath
                Specifies the path for a Virtual Hard Disk to be configured. 
                Both VHD and VHDX formats are valid.
                The Windows operating system contained within this VHD must have SIL feature installed.

.PARAMETER CertificateFilePath
                Specifies the path for the PFX file.
                
.PARAMETER CertificatePassword
                Specifies the password for the imported PFX file in the form of a secure string.

.PARAMETER SilAggregatorServer
                Specifies the SIL Aggregator server. This server must have Software Inventory Logging Aggregator 1.0 installed.

.PARAMETER SilAggregatorServerCredential
                Specifies the credentials that allow this script to connect to the remote SIL Aggregator server. 
                To obtain a PSCredential object, use the ‘Get-Credential’ cmdlet. 
                For more information, type Get-Help Get-Credential.

.LINK
                https://technet.microsoft.com/en-us/library/dn383584.aspx#BKMK_Step10

                
.EXAMPLE

    $AggregatorCreds = Get-Credential
    $CertPswd = Read-Host -AsSecureString
    
    Enable-SILCollectorVHD -VirtualHardDiskPath "E:\VHDFilePath.vhd" -CertificateFilePath "E:\Contoso.pfx" -CertificatePassword $CertPswd -SilAggregatorServer "AggregatorContoso" -SilAggregatorServerCredential $AggregatorCreds
   
 
    Software Inventory Logging configured successfully.
#> 
function Enable-SilCollectorVHD
{
Param
(
    [Parameter(Mandatory=$true, HelpMessage="Specifies the path for Virtual Hard Disk to be configured.")]
    [ValidateScript({
            if (-not ($_ -match ('\.(vhd|vhdx)$')))
            {
                throw "The VHD File Path must be of '.vhd or .vhdx' format."
            }
            Return $true
        })] $VirtualHardDiskPath,
    [Parameter(Mandatory=$true, HelpMessage="Specifies the path for the PFX file.")]
    [ValidateScript({
            if (-not ($_ -match ('\.pfx$')))
            {
                throw "The certificate must be of '.PFX' format."
            }
            Return $true
        })][string] $CertificateFilePath,
    [Parameter(Mandatory=$true, HelpMessage="Specifies the password for the imported PFX file in the form of a secure string.")]
    [ValidateNotNullorEmpty()][SecureString] $CertificatePassword,
    
    [Parameter(Mandatory=$true, HelpMessage="Specifies the SIL Aggregator server. This server must have Software Inventory Logging Aggregator 1.0 installed.")]
    [ValidateNotNullOrEmpty()][string] $SilAggregatorServer,
    
    [Parameter(Mandatory=$false, HelpMessage="Specifies the credentials that allow this script to connect to the remote SIL Aggregator server.")]
    [ValidateNotNull()][PSCredential] $SilAggregatorServerCredential
)
    try
    {
        ## Check if Script is executing from non admin PS prompt.
        if(-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator"))
        {
            throw "Error!!! login using admin credentials."
        }

        ## Check if Certificate File Path ($CertificateFilePath) is valid.
        if( -not (Test-Path $CertificateFilePath))
        {
            throw "Error!!! $CertificateFilePath is invalid."
        }

        ## Check if Virtual Hard Disk Path ($VirtualHardDiskPath) is valid.
        if( -not (Test-Path $VirtualHardDiskPath))
        {
            throw "Error!!! $VirtualHardDiskPath is invalid."
        }

        ## Check if Certficate Password is valid.
        try
        {
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
            $cert.Import($CertificateFilePath, $CertificatePassword, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)

            ## Get the thumbprint of certificate.
            $Certificatethumbprint = $cert.Thumbprint

        }
        catch 
        {
            Write-Error "Certificate Password is Incorrect."
            throw
        }

        ## local variables.
        $AggregatorAccessible = $false
        $CredentialRequired = $false
        $AggregatorInSameDomain = $false
   
        ## Test first - without Credentials.
        try
        {
            Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock {Get-Host} -ErrorAction stop | Out-Null
            $AggregatorAccessible = $true
            $CredentialRequired = $false
            $AggregatorInSameDomain = $true
        }
        catch
        {
            if ($_.Exception.ErrorCode -eq 5)
            {
                $AggregatorInSameDomain = $true
            }
        }
    
        ## Test second - with Credentials.
        if ((-not $AggregatorAccessible) -and $AggregatorInSameDomain)
        {
            try
            {
                if(-not($SilAggregatorServerCredential))
                {
                    $SilAggregatorServerCredential = $host.ui.PromptForCredential("Enter Credential", "Please enter a user credential to connect to the SIL Aggregator Server '$SilAggregatorServer'.", "", $SilAggregatorServer)
                }

                Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock {Get-Host} -ErrorAction stop | Out-Null
                $AggregatorAccessible = $true
                $CredentialRequired = $true
            }
            catch
            {
                if ($_.Exception.ErrorCode -eq 5)
                {
                    Write-Error "Error in connecting to Aggregator server $SilAggregatorServer."
                    Write-Error "$($SilAggregatorServerCredential.UserName) does not have required access, Or Password is incorrect , or PowerShell remoting is not enabled on Collector server."
                    throw
                }
            }
        }

        ## Test third - Different domain / Workgroup.
        if (-not $AggregatorAccessible)        
        {
            ## Get the current value of trusted host list.
            $trustedHostsModified = $false
            [string]$curTrustedHosts = (get-item wsman:\localhost\Client\TrustedHosts -ErrorAction Stop).value

            if ( -not $curTrustedHosts.ToLower().Contains($SilAggregatorServer.ToLower()))
            {
                if ([string]::IsNullOrEmpty($curTrustedHosts))
                {
                    $newValue = $SilAggregatorServer
                }
                else
                {
                    $newValue = [string]::Format("{0}, {1}", $curTrustedHosts, $SilAggregatorServer)
                }
                     
                set-item wsman:\localhost\Client\TrustedHosts -value $newValue -Force
                $trustedHostsModified = $true
            }

            ## Test SIL Aggregator Connection.   
            try
            {
                if(-not($SilAggregatorServerCredential))
                {
                    $SilAggregatorServerCredential = $host.ui.PromptForCredential("Enter Credential", "Please enter a user credential to connect to the SIL Aggregator Server '$SilAggregatorServer'.", "", $SilAggregatorServer)
                }

                Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock {Get-Host} -ErrorAction stop | Out-Null
                $AggregatorAccessible = $true
                $CredentialRequired = $true
                $AggregatorInSameDomain = $false
            }
            catch
            {
                if ($_.Exception.ErrorCode -eq 5)
                {
                    Write-Error "Error in connecting to remote server $SilAggregatorServer."
                    Write-Error "$($SilAggregatorServerCredential.UserName) does not have required access, Or Password is incorrect , or PowerShell remoting is not enabled on Aggregator server."
                }
                throw
            }
        }


        ## Check if SIL Aggregator installed on aggregator server machine.
        try
        {
            $SILAReportingModuleFound = $false
            if (-not $CredentialRequired)
            {
                Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock { Get-Command -Name Publish-SilReport -Module Microsoft.SilAR.PowershellCmdlets } -ErrorAction Stop | Out-null
                $SILAReportingModuleFound = $true
                     
                Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock { Get-Command -Name Get-SilAggregator -Module Microsoft.SilAR.PowershellCmdlets } -ErrorAction Stop | Out-null
            }
            else
            {
                Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock { Get-Command -Name Publish-SilReport -Module Microsoft.SilAR.PowershellCmdlets } -ErrorAction Stop | Out-null
                $SILAReportingModuleFound = $true
                     
                Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock { Get-Command -Name Get-SilAggregator -Module Microsoft.SilAR.PowershellCmdlets } -ErrorAction Stop | Out-null
            }
        }
        catch
        {
            if ($SILAReportingModuleFound)
            {
                    Write-Error "Error!!! Only Reporting Module is found on $SilAggregatorServer. Install Software Inventory Logging Aggregator."
            }
            else
            {
                    Write-Error "Error!!! Software Inventory Logging Aggregator 1.0 is not installed on $SilAggregatorServer."
            }
            throw 
        }

        ## Get TargetUri from Sil Aggregator Server.

        if (-not $CredentialRequired)
        {
            $AggregatorInfo = Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock {Get-SilAggregator} -ErrorAction Stop
        }
        else
        {
            $AggregatorInfo = Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock {Get-SilAggregator} -ErrorAction Stop
        }

        $TargetURI = $AggregatorInfo.TargetURI

        try
        {
            ## Mount the VHD disk image and get the drive letter after mount.
            $before = (Get-WMIObject -Class Win32_Volume).DriveLetter

            $osInfo = Get-WmiObject -class Win32_OperatingSystem -ErrorAction stop

            if(($osInfo.Version.StartsWith("6.2")) -or ($osInfo.Version.StartsWith("6.3")))
            {
                Mount-DiskImage -ImagePath $VirtualHardDiskPath –Passthru -ErrorAction Stop | Out-Null
            }
            else
            {
                $script = "SELECT VDISK FILE = $VirtualHardDiskPath `nATTACH VDISk"
                $script | diskpart 
            }
            $after = (Get-WMIObject -Class Win32_Volume).DriveLetter
            $driveLetters = $after | ?{-not ($before -contains $_)}
            foreach($d in $driveLetters)
            {
                if(Test-Path ($d + "\Windows\System32\config\software"))
                {
                    $driveLetter = $d
                    break;
                }
            }
            $DiskMounted = $true
        }
        catch
        {
            Write-Error "VHDFile is being used by another process." 
            throw
        }

        ## Load the remote file registry.
        $RemoteReg= $DriveLetter + "\Windows\System32\config\software"
        REG LOAD 'HKLM\REMOTEPC' $RemoteReg | Out-Null 
        $registryLoaded = $true

        ## Finally we tell the remote computer at First run to execute the Import-Pfx Command.

        ##Get the OS version
        $Os = Get-ItemProperty -Path "HKLM:\REMOTEPC\Microsoft\Windows NT\CurrentVersion" -Name "CurrentVersion"

        ## Check the OS version on collector machine and checks for updates accordingly
        ## 6.1 -> Windows server 2008 R2
        ## 6.2 -> Windows server 2012
        ## 6.3 -> Windows Server 2012 R2
        if($Os.CurrentVersion.StartsWith("6.1"))
        {
            $KB3109118 = Get-ChildItem -Path "HKLM:\REMOTEPC\Microsoft\windows\currentversion\Component Based Servicing\Packages" -Recurse | where { $_.Name.ToString().Contains('KB3109118')}   
            if(-not($KB3109118))
            {
                throw "Required Windows Update (KB3109118) is not installed on VHD."
            }
        }
        elseif($Os.CurrentVersion.StartsWith("6.2"))
        {
            $KB3119938 = Get-ChildItem -Path "HKLM:\REMOTEPC\Microsoft\windows\currentversion\Component Based Servicing\Packages" -Recurse | where { $_.Name.ToString().Contains('KB3119938')}
            if(-not($KB3119938))
            {
                throw "Required Windows Update (KB3119938) is not installed on VHD."
            }
        }
        elseif ($Os.CurrentVersion.StartsWith("6.3"))
        {
            $KB3060681 = Get-ChildItem -Path "HKLM:\REMOTEPC\Microsoft\windows\currentversion\Component Based Servicing\Packages" -Recurse | where { $_.Name.ToString().Contains('KB3060681')}
            $KB3000850 = Get-ChildItem -Path "HKLM:\REMOTEPC\Microsoft\windows\currentversion\Component Based Servicing\Packages" -Recurse | where { $_.Name.ToString().Contains('KB3000850')}
            if((-not($KB3060681)) -and (-not($KB3000850)))
            {
                throw "Required Windows Updates (KB3060681 & KB3000850) are not installed on VHD."
                    
            }
            elseif(-not($KB3060681))
            {
                throw "Required Windows Update (KB3060681) is not installed on VHD."
                     
            }
            elseif(-not($KB3000850))
            {
                throw "Required Windows Update (KB3000850) is not installed on VHD."
                     
            }
        }
        else
        {
            if(-not(Test-Path -Path "HKLM:\REMOTEPC\Microsoft\Windows\SoftwareInventoryLogging"))
            {
                Write-Error "Software Inventory Logging feature is not found. The VHD may have the Operating System which does not support SIL."
                throw
            }
        }

        ## Create directory if not exists.

        ## extract the certificate name from Certificate File path.
        $cert = [IO.Path]::GetFileName($CertificateFilePath)

        $location = $driveLetter + "\Scripts"
        if(-not(Test-Path $location))
        {     
            New-Item $location -ItemType directory |Out-Null
        }

        ## create setup File if not exixts.
        $SetupFilePath = $location + "\EnableSIL.cmd"
        if(-not(Test-Path $SetupFilePath))
        {
            New-Item $SetupFilePath -ItemType file |Out-Null
        }

        $remoteCert = $location + "\" + $cert
        $filePath = "C:\Scripts\EnableSIL.cmd"
        $certFile = "C:\Scripts\" + $cert 
        $path = "C:\Scripts"

        ## copy Certificate to VHD.
        Copy-Item -Path $CertificateFilePath -Destination $remoteCert -ErrorAction Stop -Recurse -Force

        Set-Variable -Name psPath -Value "%windir%\System32\WindowsPowerShell\v1.0\powershell.exe" -Option Constant
        Set-Variable -Name certStore -Value "cert:\localmachine\MY" -Option Constant
        $encCertPswd = ConvertFrom-SecureString -SecureString $CertificatePassword -Key (1..16)

        ## Check the OS version on collector machine and use the powershell command to copy pfx accordingly
        ## 6.1 -> Windows server 2008 R2
        ## 6.2 -> Windows server 2012
        ## 6.3 -> Windows Server 2012 R2
        if($Os.CurrentVersion.StartsWith("6.1"))
        {
            ## Command to Import pfx certificate.
            $cm1 = "set-variable -name pfx -value (new-object System.Security.Cryptography.X509Certificates.X509Certificate2)"
            $cm2 = "set-variable -name CertificateStore -value (new-object System.Security.Cryptography.X509Certificates.X509Store('MY','localmachine'))"
            $PfxCertificate = '$pfx'
            $cm3 = [String]::Format("{0}.import('{1}',(convertto-securestring -key (1..16) -string '{2}'),'Exportable,PersistKeySet')", $PfxCertificate, $certFile, $encCertPswd)
            $CertificateStore = '$CertificateStore'
            $cm4 = [String]::Format("{0}.open('MaxAllowed')", $CertificateStore)
            $cm5 = [String]::Format('$CertificateStore.add($pfx)')
            $cm6 = [String]::Format('$CertificateStore.close()')
            $cmd = [String]::Format("{0}; {1}; {2}; {3}; {4}; {5}", $cm1, $cm2, $cm3, $cm4, $cm5, $cm6)
        }
        else
        {
            ## Command to Import pfx certificate.            
            $cmd = "Import-PfxCertificate -CertStoreLocation ""$certStore"" -FilePath ""$certFile"" -Password (convertto-securestring -key (1..16) -string ""$encCertPswd"")"
        }

        $cmd1 = [string]::Format("{0} -command {1}", $pspath, $cmd) 
        Add-Content $SetupFilePath $cmd1

        ## Command to Remove Certificate.            
        $cmd = "Remove-Item ""$path"" -Force -Recurse -ErrorAction Stop"

        $cmd2 = [string]::Format("{0} -command {1}", $pspath, $cmd) 
        Add-Content $SetupFilePath "`n$cmd2"

        $cmd3 = [string]::Format("{0} -command {1}", $pspath, "Stop-SilLogging") 
        Add-Content $SetupFilePath "`n$cmd3"

        $cmd4 = [string]::Format("{0} -command {1}", $pspath, "Start-SilLogging") 
        Add-Content $SetupFilePath "`n$cmd4"

        SET-ITEMPROPERTY "HKLM:\REMOTEPC\Microsoft\Windows\CurrentVersion\RunOnce\" -Name "EnableSilLogging" -Value $filePath -Force
                              
        ## Set the TargetUri into Sillogging registry.
        SET-ITEMPROPERTY -Path "HKLM:\REMOTEPC\Microsoft\Windows\SoftwareInventoryLogging" -Name "TargetUri" -Value $TargetUri -Force

        ## Set the Cetificate Thumprint into Sillogging registry.
        SET-ITEMPROPERTY -Path "HKLM:\REMOTEPC\Microsoft\Windows\SoftwareInventoryLogging" -Name "CertificateThumbprint" -Value $CertificateThumbprint -Force

        ## Set the CollectionState as running state.
        SET-ITEMPROPERTY "HKLM:\REMOTEPC\Microsoft\Windows\SoftwareInventoryLogging\" -Name "CollectionState" -Value 1 -Force  
        $SILEnabled = $true 

        ## Script Block to execute Set-SilAggregator command on sil Aggregator server.
        $sbAggregator = {
                param($tu)
                Set-SilAggregator –AddCertificateThumbprint $tu -Force
              }

        ## Run Set-SilAggregator command on aggregator server.
        if($SilEnabled)
        {
            if (-not $CredentialRequired)
            {
                    Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock $sbAggregator -ArgumentList $Certificatethumbprint -ErrorAction Stop | Out-Null
            }
            else
            {
                    Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock $sbAggregator -ArgumentList $Certificatethumbprint -ErrorAction Stop | Out-Null
            }
        }       
    }
    catch
    {
        throw $_.Exception
    }

    finally
    {
        ## Revert back the trusted host settings. 
        if ($trustedHostsModified)
        {
               set-item wsman:\localhost\Client\TrustedHosts -value $curTrustedHosts -Force
        }

        if($registryLoaded)
        {
            Remove-Variable * -Exclude DiskMounted, osInfo, SilEnabled -ErrorAction SilentlyContinue
            [gc]::Collect()
            [gc]::WaitForPendingFinalizers()
            ## Unload the file registry.
            REG UNLOAD 'HKLM\REMOTEPC' | Out-Null
        }

        if($DiskMounted)
        {
            ## Dismount the VHD 
            if(($osInfo.Version.StartsWith("6.2")) -or ($osInfo.Version.StartsWith("6.3")))
            {
                Dismount-DiskImage $VirtualHardDiskPath | Out-Null
            }
            else
            {
                $script = "SELECT VDISK FILE = $VirtualHardDiskPath  `ndetach vdisk"
                $script | diskpart 
            } 
        }
    }

    if($SILEnabled)
    {
        ## Display Success Message.
        Write-Host "Software Inventory Logging configured successfully."
    }
        
}

<#
.Synopsis
  Enable and configure Software Inventory Logging in a Virtual Hard Disk.

.Description
  This script updates %WINDIR%\Setup\Scripts\SetupComplete.cmd in the given VHD. 
    When a new VM is created using this VHD, the Software Inventory Logging is configured 
	after Windows is installed, but before the logon screen appears.

.INPUTS

.OUTPUTS

.NOTES

  Author   : Microsoft
  Date     : 2015/12/28
  Version  : 1.0
  
  Updates:

.PARAMETER VirtualHardDiskPath
                Specifies the path for Virtual Hard Disk to be configured.

.PARAMETER CertificateFilePath
                Specifies the path for the PFX file.
                
.PARAMETER CertificatePassword
                Specifies the password for the imported PFX file in the form of a secure string.

.PARAMETER SilAggregatorServer
                Specifies the SIL Aggregator server. This server must have Software Inventory Logging Aggregator 1.0 installed.

.PARAMETER SilAggregatorServerCredential
                Specifies the credentials that allow this script to connect to the remote SIL Aggregator server. 
                To obtain a PSCredential object, use the ‘Get-Credential’ cmdlet. 
                For more information, type Get-Help Get-Credential.

.LINK
                https://technet.microsoft.com/en-us/library/dn268301.aspx
                
.EXAMPLE
    $AggregatorCreds = Get-Credential
    $CertPswd = Read-Host -AsSecureString
    
    Enable-SilCollectorWithWindowsSetup -VirtualHardDiskPath 'C:\Contoso.vhd' -SilAggregatorServer AggregatorContoso -SilAggregatorServerCredential $AggregatorCreds -CertificateFilePath "C:\Users\User1\Desktop\Contoso.pfx -CertificatePassword $CertPswd

    This command configures Software Inventory Logging on VHD file.
#>
function Enable-SILCollectorWithWindowsSetup
{   

Param
(
    [Parameter(Mandatory=$true, HelpMessage="Specifies the path for Virtual Hard Disk to be configured.")]
    [ValidateScript({
            if (-not ($_ -match ('\.(vhd|vhdx)$')))
            {
                throw "The VHD File Path must be of '.vhd or .vhdx' format."
            }
            return $true
        })] $VirtualHardDiskPath,

    [Parameter(Mandatory=$true, HelpMessage="Specifies the path for the PFX file.")]
    [ValidateScript({
            if (-not ($_ -match ('\.pfx$')))
            {
                throw "The certificate must be of '.PFX' format."
            }
            Return $true
        })][string] $CertificateFilePath,
    [Parameter(Mandatory=$true, HelpMessage="Specifies the password for the imported PFX file in the form of a secure string.")]
    [SecureString] $CertificatePassword,

    [Parameter(Mandatory=$true, HelpMessage="Specifies the SIL Aggregator server. This server must have Software Inventory Logging Aggregator 1.0 installed.")]
    [ValidateNotNullOrEmpty()][string] $SilAggregatorServer,

    [Parameter(Mandatory=$false, HelpMessage="Specifies the credentials that allow this script to connect to the remote SIL Aggregator server.")]
    [ValidateNotNull()][PSCredential] $SilAggregatorServerCredential

   
) 
    try
    {
        ## Check if Script is not executing from non admin PS prompt
        if(-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator"))
        {
            throw "Error!!! login using admin credentials."               
        }

        ## Check if Certificate File Path ($CertificateFilePath) is valid.
        if( -not (Test-Path $CertificateFilePath))
        {
            throw "Error!!! $CertificateFilePath is invalid."            
        }

       ## Check if Virtual Hard Disk Path ($VirtualHardDiskPath) is valid.
        if( -not (Test-Path $VirtualHardDiskPath))
        {
            throw "Error!!! $VirtualHardDiskPath is invalid."            
        }
    
        ## Check if Certficate Password is valid.
        try
        {
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
            $cert.Import($CertificateFilePath, $CertificatePassword, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)

            ## Get the thumbprint of certificate
            $Certificatethumbprint = $cert.Thumbprint
        }
        catch 
        {
            Write-Error "Certificate Password is Incorrect."
            throw
            
        }	   

        ## local variables
	    $AggregatorAccessible = $false
	    $CredentialRequired = $false
	    $AggregatorInSameDomain = $false
	

	    ## Test first - without Credentials.
	    try
        {
            Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock {Get-Host} -ErrorAction stop | Out-Null
		    $AggregatorAccessible = $true
		    $CredentialRequired = $false
		    $AggregatorInSameDomain = $true
        }
	    catch
	    {
		    ## Current user does not have required access or remoting is disabled.
            if ($_.Exception.ErrorCode -eq 5)
		    {
			    $AggregatorInSameDomain = $true
		    }
	    }

	    ## Test second - with Credentials.
	    if ((-not $AggregatorAccessible) -and $AggregatorInSameDomain)
	    {
		    try
		    {
			   
                if(-not($SilAggregatorServerCredential))
                {
                    $SilAggregatorServerCredential = $host.ui.PromptForCredential("Enter Credential", "Please enter a user credential to connect to the SIL Aggregator Server '$SilAggregatorServer'.", "", $SilAggregatorServer)
                }

                Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock {Get-Host} -ErrorAction stop | Out-Null
                $AggregatorAccessible = $true
                $CredentialRequired = $true
		    }
		    catch
		    {
			    if ($_.Exception.ErrorCode -eq 5)
                {
                    Write-Error "Error in connecting to Aggregator server $SilAggregatorServer."
                    Write-Error "$($SilAggregatorServerCredential.UserName) does not have required access, Or Password is incorrect , or PowerShell remoting is not enabled on Collector server."
                    throw
                }
		    }
	    }

        ## Test third - Different domain / Workgroup.
	    if (-not $AggregatorAccessible)
	    {
		     ## Get the current value of trusted host list.
            $trustedHostsModified = $false
            [string]$curTrustedHosts = (get-item wsman:\localhost\Client\TrustedHosts -ErrorAction Stop).value

            if ( -not $curTrustedHosts.ToLower().Contains($SilAggregatorServer.ToLower()))
            {
                if ([string]::IsNullOrEmpty($curTrustedHosts))
                {
                    $newValue = $SilAggregatorServer
                }
                else
                {
                    $newValue = [string]::Format("{0}, {1}", $curTrustedHosts, $SilAggregatorServer)
                }
                     
                set-item wsman:\localhost\Client\TrustedHosts -value $newValue -Force
                $trustedHostsModified = $true
            }

            ## Test SIL Aggregator Connection.   
            try
            {
                if(-not($SilAggregatorServerCredential))
                {
                    $SilAggregatorServerCredential = $host.ui.PromptForCredential("Enter Credential", "Please enter a user credential to connect to the SIL Aggregator Server '$SilAggregatorServer'.", "", $SilAggregatorServer)
                }

                Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock {Get-Host} -ErrorAction stop | Out-Null
                $AggregatorAccessible = $true
                $CredentialRequired = $true
                $AggregatorInSameDomain = $false
            }
            catch
            {
                if ($_.Exception.ErrorCode -eq 5)
                {
                    Write-Error "Error in connecting to remote server $SilAggregatorServer."
                    Write-Error "$($SilAggregatorServerCredential.UserName) does not have required access, Or Password is incorrect , or PowerShell remoting is not enabled on Aggregator server."
                }
                throw
            }
	    }
		
        ## Check if SIL Aggregator installed on aggregator server machine.
        try
        {
            $SILAReportingModuleFound = $false
            if (-not $CredentialRequired)
            {
                Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock { Get-Command -Name Publish-SilReport -Module Microsoft.SilAR.PowershellCmdlets } -ErrorAction Stop | Out-null
                $SILAReportingModuleFound = $true
                     
                Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock { Get-Command -Name Get-SilAggregator -Module Microsoft.SilAR.PowershellCmdlets } -ErrorAction Stop | Out-null
            }
            else
            {
                Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock { Get-Command -Name Publish-SilReport -Module Microsoft.SilAR.PowershellCmdlets } -ErrorAction Stop | Out-null
                $SILAReportingModuleFound = $true
                     
                Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock { Get-Command -Name Get-SilAggregator -Module Microsoft.SilAR.PowershellCmdlets } -ErrorAction Stop | Out-null
            }
        }
        catch
        {
            if ($SILAReportingModuleFound)
            {
                    Write-Error "Error!!! Only Reporting Module is found on $SilAggregatorServer. Install Software Inventory Logging Aggregator."
            }
            else
            {
                    Write-Error "Error!!! Software Inventory Logging Aggregator 1.0 is not installed on $SilAggregatorServer."
            }
            throw 
        }

        ## Get TargetUri from Sil Aggregator Server.

        if (-not $CredentialRequired)
        {
            $AggregatorInfo = Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock {Get-SilAggregator} -ErrorAction Stop
        }
        else
        {
            $AggregatorInfo = Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock {Get-SilAggregator} -ErrorAction Stop
        }

        $TargetURI = $AggregatorInfo.TargetURI


       try
        {
            ## Mount the VHD disk image and get the drive letter after mount.
            $before = (Get-WMIObject -Class Win32_Volume).DriveLetter

            $osInfo = Get-WmiObject -class Win32_OperatingSystem -ErrorAction stop

            if(($osInfo.Version.StartsWith("6.2")) -or ($osInfo.Version.StartsWith("6.3")))
            {
                Mount-DiskImage -ImagePath $VirtualHardDiskPath –Passthru -ErrorAction Stop | Out-Null
            }
            else
            {
                $script = "SELECT VDISK FILE = $VirtualHardDiskPath `nATTACH VDISk"
                $script | diskpart 
            }
            $after = (Get-WMIObject -Class Win32_Volume).DriveLetter
            $driveLetters = $after | ?{-not ($before -contains $_)}
            foreach($d in $driveLetters)
            {
                if(Test-Path ($d + "\Windows\System32\config\software"))
                {
                    $driveLetter = $d
                    break;
                }
            }
            $DiskMounted = $true
        }
        catch
        {
            Write-Error "VHDFile is being used by another process." 
            throw
        }

        ## Load the remote file registry.
        $RemoteReg= $DriveLetter + "\Windows\System32\config\software"
        REG LOAD 'HKLM\REMOTEPC' $RemoteReg | Out-Null 
        $registryLoaded = $true

        ## Finally we tell the remote computer at First run to execute the Import-Pfx Command.
       
        ##Get the OS version
        $Os = Get-ItemProperty -Path "HKLM:\REMOTEPC\Microsoft\Windows NT\CurrentVersion" -Name "CurrentVersion"

        ## Check the OS version on collector machine and checks for updates accordingly
        ## 6.1 -> Windows server 2008 R2
        ## 6.2 -> Windows server 2012
        ## 6.3 -> Windows Server 2012 R2
        if($Os.CurrentVersion.StartsWith("6.1"))
        {
            $KB3109118 = Get-ChildItem -Path "HKLM:\REMOTEPC\Microsoft\windows\currentversion\Component Based Servicing\Packages" -Recurse | where { $_.Name.ToString().Contains('KB3109118')}   
            if(-not($KB3109118))
            {
                throw "Required Windows Update (KB3109118) is not installed on VHD."
            }
        }
        elseif($Os.CurrentVersion.StartsWith("6.2"))
        {
            $KB3119938 = Get-ChildItem -Path "HKLM:\REMOTEPC\Microsoft\windows\currentversion\Component Based Servicing\Packages" -Recurse | where { $_.Name.ToString().Contains('KB3119938')}
            if(-not($KB3119938))
            {
                throw "Required Windows Update (KB3119938) is not installed on VHD."
            }
        }
        elseif ($Os.CurrentVersion.StartsWith("6.3"))
        {
            $KB3060681 = Get-ChildItem -Path "HKLM:\REMOTEPC\Microsoft\windows\currentversion\Component Based Servicing\Packages" -Recurse | where { $_.Name.ToString().Contains('KB3060681')}
            $KB3000850 = Get-ChildItem -Path "HKLM:\REMOTEPC\Microsoft\windows\currentversion\Component Based Servicing\Packages" -Recurse | where { $_.Name.ToString().Contains('KB3000850')}
            if((-not($KB3060681)) -and (-not($KB3000850)))
            {
                throw "Required Windows Updates (KB3060681 & KB3000850) are not installed on VHD."
                    
            }
            elseif(-not($KB3060681))
            {
                throw "Required Windows Update (KB3060681) is not installed on VHD."
                     
            }
            elseif(-not($KB3000850))
            {
                throw "Required Windows Update (KB3000850) is not installed on VHD."
                     
            }
        }
        else
        {
            if(-not(Test-Path -Path "HKLM:\REMOTEPC\Microsoft\Windows\SoftwareInventoryLogging"))
            {
                Write-Error "Software Inventory Logging feature is not found. The VHD may have the Operating System which does not support SIL."
                throw
            }
        }

        ## Create directory if not exists.
        $location = $driveLetter + "\Windows\Setup\Scripts"
        if(-not(Test-Path $location))
        {
			New-Item $location -ItemType directory | Out-Null
        }
		
		## Copy Certificate.
		Copy-Item -Path $CertificateFilePath -Destination $location -ErrorAction Stop -Recurse -Force

		## Create SetupComplete.cmd file.
		$setupFile = $location + "\SetupComplete.cmd"
		
        if(-not(Test-path $setupFile))
		{
			New-Item -Path $setupFile -ItemType File | Out-Null
		}

        ## Update SetupComplete.cmd file.

        Set-Variable -Name psPath -Value "%windir%\System32\WindowsPowerShell\v1.0\powershell.exe" -Option Constant
        Set-Variable -Name certStore -Value "cert:\localmachine\MY" -Option Constant
    
        ## Encrypt certificate password.
        $encCertPswd = ConvertFrom-SecureString -SecureString $CertificatePassword -Key (1..16)
        
        $i = $CertificateFilePath.LastIndexOf("\")
        $cert = $CertificateFilePath.Substring($i + 1)
        $certFile = "%windir%\Setup\Scripts\" + $cert
            
        ## Check the OS version on collector machine and use the powershell command to copy pfx accordingly
        ## 6.1 -> Windows server 2008 R2
        ## 6.2 -> Windows server 2012
        ## 6.3 -> Windows Server 2012 R2
        if($Os.CurrentVersion.StartsWith("6.1"))
        {
            ## Command to Import pfx certificate.
            $cm1 = "set-variable -name pfx -value (new-object System.Security.Cryptography.X509Certificates.X509Certificate2)"
            $cm2 = "set-variable -name CertificateStore -value (new-object System.Security.Cryptography.X509Certificates.X509Store('MY','localmachine'))"
            $PfxCertificate = '$pfx'
            $cm3 = [String]::Format("{0}.import('{1}',(convertto-securestring -key (1..16) -string '{2}'),'Exportable,PersistKeySet')", $PfxCertificate, $certFile, $encCertPswd)
            $CertificateStore = '$CertificateStore'
            $cm4 = [String]::Format("{0}.open('MaxAllowed')", $CertificateStore)
            $cm5 = [String]::Format('$CertificateStore.add($pfx)')
            $cm6 = [String]::Format('$CertificateStore.close()')
            $cmd = [String]::Format("{0}; {1}; {2}; {3}; {4}; {5}", $cm1, $cm2, $cm3, $cm4, $cm5, $cm6)
        }
        else
        {  
            ## Command to import pfx certificate.
            $cmd = "Import-PfxCertificate -CertStoreLocation ""$certStore"" -FilePath ""$certFile"" -Password (convertto-securestring -key (1..16) -string ""$encCertPswd"")"
        }

        $cmd1 = [string]::Format("{0} -command {1}", $pspath, $cmd)  
        Add-Content $setupFile $cmd1

        ## set-sillogging command.
        $cmd = [string]::Format("{0} -TargetURI {1} -CertificateThumbprint {2}", "Set-SilLogging" , $TargetURI, $Certificatethumbprint)
       
        $cmd2 = [string]::Format("{0} -command {1}", $pspath, $cmd) 
        Add-Content $setupFile "`n$cmd2"

        ## start-sillogging command.
        $cmd = [string]::Format("{0}", "Start-SilLogging")
       
        $cmd3 = [string]::Format("{0} -command {1}", $pspath, $cmd)
    
        Add-Content $setupFile "`n$cmd3"

        $SilEnabled = $true

            ## Script Block to execute Set-SilAggregator command on sil Aggregator server.
            $sbAggregator = {
            param($tu)
            Set-SilAggregator –AddCertificateThumbprint $tu -Force
        }

        ## Run Set-SilAggregator command on aggregator server.
        if($SilEnabled)
        {
            if (-not $CredentialRequired)
            {
                    Invoke-Command -ComputerName $SilAggregatorServer -ScriptBlock $sbAggregator -ArgumentList $Certificatethumbprint -ErrorAction Stop | Out-Null
            }
            else
            {
                    Invoke-Command -ComputerName $SilAggregatorServer -Credential $SilAggregatorServerCredential -ScriptBlock $sbAggregator -ArgumentList $Certificatethumbprint -ErrorAction Stop | Out-Null
            }
        }     
    }
    catch
    {
        throw $_.Exception
    }
    finally
    {
         ## Revert back the trusted host settings. 
        if ($trustedHostsModified)
        {
               set-item wsman:\localhost\Client\TrustedHosts -value $curTrustedHosts -Force
        }

        if($registryLoaded)
        {
            Remove-Variable * -Exclude DiskMounted, osInfo, SilEnabled -ErrorAction SilentlyContinue
            [gc]::Collect()
            [gc]::WaitForPendingFinalizers()
            ## Unload the file registry.
            REG UNLOAD 'HKLM\REMOTEPC' | Out-Null
        }

        if($DiskMounted)
        {
            ## Dismount the VHD 
            if(($osInfo.Version.StartsWith("6.2")) -or ($osInfo.Version.StartsWith("6.3")))
            {
                Dismount-DiskImage $VirtualHardDiskPath | Out-Null
            }
            else
            {
                $script = "SELECT VDISK FILE = $VirtualHardDiskPath  `ndetach vdisk"
                $script | diskpart 
            } 
        }
    }
    if($SilEnabled)
    {
        ## Display Success Message.
        Write-Host "Software Inventory Logging configured successfully."
    }
}

# Function to get access mark from permission
function Get-AccessMaskFromPermission($permissions) {

    $WBEM_ENABLE = 1

    $WBEM_METHOD_EXECUTE = 2

    $WBEM_FULL_WRITE_REP = 4

    $WBEM_PARTIAL_WRITE_REP = 8

    $WBEM_WRITE_PROVIDER = 0x10

    $WBEM_REMOTE_ACCESS = 0x20

    $WBEM_RIGHT_SUBSCRIBE = 0x40

    $WBEM_RIGHT_PUBLISH = 0x80

    $READ_CONTROL = 0x20000

    $WRITE_DAC = 0x40000

    $WBEM_RIGHTS_FLAGS = $WBEM_ENABLE,$WBEM_METHOD_EXECUTE,$WBEM_FULL_WRITE_REP,`

    $WBEM_PARTIAL_WRITE_REP,$WBEM_WRITE_PROVIDER,$WBEM_REMOTE_ACCESS,`

    $READ_CONTROL,$WRITE_DAC

    $WBEM_RIGHTS_STRINGS = "Enable","MethodExecute","FullWrite","PartialWrite",`
        "ProviderWrite","RemoteAccess","ReadSecurity","WriteSecurity"

    $permissionTable = @{}

    for ($i = 0; $i -lt $WBEM_RIGHTS_FLAGS.Length; $i++) {

        $permissionTable.Add($WBEM_RIGHTS_STRINGS[$i].ToLower(), $WBEM_RIGHTS_FLAGS[$i])

    }

    $accessMask = 0

    foreach ($permission in $permissions) {

        if (-not $permissionTable.ContainsKey($permission.ToLower())) {

        throw "Unknown permission: $permission`nValid permissions: $($permissionTable.Keys)"

        }

        $accessMask += $permissionTable[$permission.ToLower()]

    }

    $accessMask

}

<#
.Synopsis
  Sets the just enough permissions for a domain user on the host to be used as SILA Polling Account.

.Description
  This function adds the provided domain user account into the Remote Management Users group, Hyper-V administrators group and gives read only access
  to the root\CIMV2 namespace for Polling to work.


.INPUTS

.OUTPUTS

.NOTES

  Author: Microsoft
  Date  : 2016/02/29
  Vers  : 1.0
  
  Updates:


.PARAMETER computerName
                Name of the target Hyper-V host that SILA will Poll.

.PARAMETER domain
                Name of the domain the target host is joined to.
                
.PARAMETER user
                A valid user in the domain that the host is joined to. This need not be administrator on the host machine.

.PARAMETER targetMachineCredential
                Credentials of an administrator on the target host machine, this will be used to set appropriate permissions for the user provided.


               
.EXAMPLE

    $targetMachineCredential = Get-Credential
       
    Set-SILAPollingAccount -computername Contoso1 -domain Contosodomain -user existingDomainUser -targetMachineCredential $targetMachineCredential   
#> 
function Set-SILAPollingAccount
{
Param ( 
    
    [parameter(Mandatory=$true,Position=1)][string] $computername,
    
    [parameter(Mandatory=$true,Position=2)][string] $domain,
      
    [parameter(Mandatory=$true,Position=3)][string] $user,
     
    [parameter(Position=4)][PSCredential] $targetMachineCredential
      
)


    $errorActionPreference = "Stop"

    $namespace = 'root/cimv2'

    $permissions = 'RemoteAccess' 

    $ErrorActionPreference = "SilentlyContinue"
    
    $group = [ADSI]("WinNT://$computername/Remote Management Users,group")

    $group.add("WinNT://$domain/$user,user")

    $group = [ADSI]("WinNT://$computername/Hyper-V Administrators,group")

    $group.add("WinNT://$domain/$user,user")

    $ErrorActionPreference = "Continue"
          
    if(($computername -notmatch $env:computerName) -and ($targetMachineCredential -ne $null))
    {
      $PSBoundParameters.Add("Credential", $targetMachineCredential)
    }
    

if ($PSBoundParameters.ContainsKey("Credential")) {

    $remoteparams = @{ComputerName=$computerName;Credential=$targetMachineCredential}

    } else {

    $remoteparams = @{ComputerName=$computerName}

}

    $invokeparams = @{Namespace=$namespace;Path="__systemsecurity=@"} + $remoteParams

    $output = Invoke-WmiMethod @invokeparams -Name GetSecurityDescriptor

if ($output.ReturnValue -ne 0) {

    throw "GetSecurityDescriptor failed: $($output.ReturnValue)"

}

    $acl = $output.Descriptor   

    $getparams = @{Class="Win32_Account";Filter="Domain='$domain' and Name='$user'"}

    $win32account = Get-WmiObject @getparams 

if ($win32account -eq $null) {

      throw "Account was not found: $account"

}

    $accessMask = Get-AccessMaskFromPermission($permissions)

    $ace = (New-Object System.Management.ManagementClass("win32_Ace")).CreateInstance()

    $ace.AccessMask = $accessMask

    $ace.AceFlags = 0

    $trustee = (New-Object System.Management.ManagementClass("win32_Trustee")).CreateInstance()

    $trustee.SidString = $win32account.Sid

    $ace.Trustee = $trustee

    $ACCESS_ALLOWED_ACE_TYPE = 0x0

    $ace.AceType = $ACCESS_ALLOWED_ACE_TYPE

    $acl.DACL += $ace.psobject.immediateBaseObject

    $setparams = @{Name="SetSecurityDescriptor";ArgumentList=$acl.psobject.immediateBaseObject} + $invokeParams

    $output = Invoke-WmiMethod @setparams

if ($output.ReturnValue -ne 0) {

        throw "SetSecurityDescriptor failed: $($output.ReturnValue)"

}

}