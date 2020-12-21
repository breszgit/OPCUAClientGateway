Command for convert to single exe.
 - dotnet publish -c Release -r win10-x64 -p:PublishSingleFile=true

Deploy
 - Copy appsettings.json and Opc.xml to root Application.
 
Check Certificate File
 1.Go to "C:\Users\%Username%\AppData\Local\OPC Foundation\pki\rejected"
 2.Move all file in folder1. to "C:\Users\%Username%\AppData\Local\OPC Foundation\pki\trusted\certs"
