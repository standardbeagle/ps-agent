@{
    RootModule           = 'PsAgent.Cmdlets.dll'
    ModuleVersion        = '0.1.0'
    GUID                 = 'b1f6b0f2-8a2e-4d4a-9f0a-2c1d6e7a4f10'
    Author               = 'andylbrummer'
    CompanyName          = 'StandardBeagle'
    Description          = 'A minimal coding agent (Invoke-Agent) and an Agent Client Protocol client (Invoke-Acp), both rendered through Show-Styled''s Strata stylesheet cascade.'
    PowerShellVersion    = '7.4'

    CmdletsToExport      = @('Invoke-Agent', 'Invoke-Acp')
    FunctionsToExport    = @()
    VariablesToExport    = @()
    AliasesToExport      = @('agent', 'pia', 'acp')

    PrivateData = @{
        PSData = @{
            Tags         = @('agent', 'acp', 'claude', 'llm', 'tui', 'strata')
            ProjectUri   = 'https://github.com/standardbeagle/ps-agent'
            ReleaseNotes = '0.1.0: Invoke-Agent (Anthropic Messages API tool loop) and Invoke-Acp (Agent Client Protocol client), sharing one Strata-rendered transcript viewer.'
        }
    }
}
