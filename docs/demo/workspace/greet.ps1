function Get-Greeting {
    param([string[]] $Names)

    for ($i = 0; $i -le $Names.Count; $i++) {
        "Hello, $($Names[$i])!"
    }
}
