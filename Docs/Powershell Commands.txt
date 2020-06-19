##### Execute or run any file as a script file #####
Invoke-FileAsScript -Path C:\Temp\Text.txt

##### Remove Item #####
rm .\subDir\a.png, .\anotherDir\b.jpg, .\thirdDir\c.gif
rm .\subDir\a.png, .\anotherDir\b.jpg, .\thirdDir\c.gif
echo subDir/a.png anotherDir/b.jpg thirdDir/c.gif | rm

##### command puts contents on clipboard #####
ls | clip
pwd | clip
cat ~/.ssh/id_rsa.pub | clip

##### command puts contents to file #####
YOUR-COMMAND | Out-File -FilePath c:\PATH\TO\FOLDER\OUTPUT.txt
  ### View saved output on screen ###
Get-Content -Path c:\PATH\TO\FOLDER\OUTPUT.txt

##### command puts contents of myfile.txt on clipboard #####
gc .\myfile.txt | scb

########### Compare 2 files #############
Compare-Object ... | Out-File -Encoding utf8 C:\filename.txt

Compare-Object $(Get-Content c:\user\documents\List1.txt) $(Get-Content c:\user\documents\List2.txt) 

Compare-Object $(Get-Content c:\user\documents\List1.txt) $(Get-Content c:\user\documents\List2.txt) > c:\user\documents\diff_output.txt


# Create a file in the current directory
New-Item -Path . -Name "testfile1.txt" -ItemType "file" -Value "This is a text string."

# Create a directory
New-Item -Path "c:\" -Name "logfiles" -ItemType "directory"

# Create a profile
New-Item -Path $profile -ItemType "file" -Force

# Create a directory in a different directory
New-Item -ItemType "directory" -Path "c:\ps-test\scripts"

# Create multiple files
New-Item -ItemType "file" -Path "c:\ps-test\test.txt", "c:\ps-test\Logs\test.log"

# Use wildcards to create files in multiple directories
Get-ChildItem -Path C:\Temp\

Directory:  C:\Temp

Mode                LastWriteTime     Length Name
----                -------------     ------ ----
d-----        5/15/2019   6:45 AM        1   One
d-----        5/15/2019   6:45 AM        1   Two
d-----        5/15/2019   6:45 AM        1   Three

New-Item -Path * -Name temp.txt -ItemType File | Select-Object FullName

FullName
--------
C:\Temp\One\temp.txt
C:\Temp\Three\temp.txt
C:\Temp\Two\temp.txt

# Create a symbolic link to a file or folder
$link = New-Item -ItemType SymbolicLink -Path .\link -Target .\Notice.txt
$link | Select-Object LinkType, Target

LinkType     Target
--------     ------
SymbolicLink {.\Notice.txt}



 




Function Get-MyCommands {
    Get-Content -Path $profile | Select-String -Pattern "^function.+" | ForEach-Object {
        [Regex]::Matches($_, "^function ([a-z.-]+)","IgnoreCase").Groups[1].Value
    } | Where-Object { $_ -ine "prompt" } | Sort-Object
}

#####################################################################Get list of what's running on computer. Process objects are sent to Out-File cmdlet. Out-File uses FilePath and creates Process.txt. Get-Content gets content from the file & displays it in the PowerShell console.################################################# 
Get-Process | Out-File -FilePath .\Process.txt
Get-Content -Path .\Process.txt

########### Encode output with a specific encoding type #############
$Procs = Get-Process
Out-File -FilePath .\Process.txt -InputObject $Procs -Encoding ASCII -Width 50




Set-Location -Path Alias:
Get-Location
Get-ChildItem | Out-File -FilePath C:\TestDir\AliasNames.txt
Get-Content -Path C:\TestDir\AliasNames.txt









# Starts a transcript in default file location.
Start-Transcript

# Start a transcript file at a specific location
Start-Transcript -Path "C:\transcripts\transcript0.txt" -NoClobber

# This command starts a transcript in the Transcript0.txt file in C:\transcripts. Since the NoClobber parameter is used, the command prevents any existing files from being overwritten. If the Transcript0.txt file already exists, the command fails.

# Stop all transcripts
Stop-Transcript
