@{ 
# Version number of this module. 
 ModuleVersion = '0.0.1' 

 ModuleToProcess = "SILDeploymentHelper.psm1"
 
# ID used to uniquely identify this module 
 GUID = '74C76B26-E9D2-4EBA-89E4-6D014A852A40' 

 
 # Author of this module 
 Author = 'Microsoft Corporation' 

 
# Company or vendor of this module 
 CompanyName = 'Microsoft Corporation' 
 
 
 # Copyright statement for this module 
 Copyright = '(c) 2016 Microsoft Corporation. All rights reserved.' 
 
 
 # Description of the functionality provided by this module 
 Description = 'SIL Deployment Helper' 

 
 # Minimum version of the Windows PowerShell engine required by this module 
 PowerShellVersion = '4.0' 
 
 
 # Minimum version of the common language runtime (CLR) required by this module 
CLRVersion = '4.0' 

 
 # Private data to pass to the module specified in RootModule/ModuleToProcess. This may also contain a PSData hashtable with additional module metadata used by PowerShell. 
 PrivateData = @{ 
 
 
 PSData = @{ 

 
 # Tags applied to this module. These help with module discovery in online galleries. 
 Tags = @('SILDeployment', 'SIL', 'SILAggregator') 

 
# A URL to the license for this module. 
LicenseUri = '' 
 
 # A URL to the main website for this project. 
 ProjectUri = '' 

# A URL to an icon representing this module. 
 # IconUri = '' 

# ReleaseNotes of this module 
 # ReleaseNotes = '' 

} # End of PSData hashtable 

 
} # End of PrivateData hashtable 

 
# Functions to export from this module 
FunctionsToExport = "Enable-SILCollector","Enable-SilCollectorVHD","Enable-SILCollectorWithWindowsSetup","Set-SILAPollingAccount" 

 
# Cmdlets to export from this module 
CmdletsToExport = '*' 
} 
