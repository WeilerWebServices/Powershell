# PowerShell ISE Keymap for Visual Studio Code

This extension ports the most popular PowerShell ISE keyboard shortcuts to Visual Studio Code. After installing the extension and restarting VS Code these keyboard shortcuts will be enabled.

## What keyboard shortcuts are included?

You can see all the keyboard shortcuts in the extension's contribution list. 

## How do I contribute a keyboard shortcut?

We may have missed a keyboard shortcut. If we did please help us out! It is very easy to make a PR. 

1. Head over to our [GitHub repository](https://github.com/PowerShell/vscode-powershellise-keybindings). 
2. Open the [`package.json` file](https://github.com/PowerShell/vscode-powershellise-keybindings/blob/master/package.json). 
3. Add a JSON object to [`contributes.keybindings`](https://github.com/PowerShell/vscode-powershellise-keybindings/blob/master/package.json#L31) as seen below. 
4. Open a pull request. 

```json
{
    "mac": "<keyboard shortcut for mac>",
    "linux": "<keyboard shortcut for linux>",
    "win": "<keyboard shortcut for windows>",
    "key": "<default keyboard shortcut>",
    "command": "<name of the command in VS Code"
}
```

You can read more about how to contribute keybindings in extensions in the [official documentation](http://code.visualstudio.com/docs/extensionAPI/extension-points#_contributeskeybindings). 

# Contributing

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
