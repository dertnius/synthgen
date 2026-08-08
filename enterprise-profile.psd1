@{
    # SynthGen enterprise profile — consumed by scripts/setup-enterprise.ps1.
    #
    # This file is COMMITTED with placeholder values. In your enterprise fork, edit it
    # in place with your internal endpoints and commit — every teammate then gets a
    # zero-prompt setup. Machine-specific values (proxy, CA bundle path) belong in the
    # gitignored enterprise-profile.local.psd1 (same schema, omit what you don't set)
    # or on the command line; precedence is: flags > local override > this file.

    # Bump only when the schema changes; the wizard refuses unknown versions.
    ProfileVersion = 1

    # 'mirror'  = internal NuGet/conda remotes reachable over the network
    # 'offline' = fully air-gapped: NuGet folder feed + conda package cache
    # ''        = wizard prompts interactively (or fails fast with -NonInteractive)
    Mode = ''

    NuGet = @{
        # Mirror mode: v3 index URL of your internal proxy of nuget.org, e.g.
        # 'https://artifacts.corp.example/api/nuget/v3/nuget-remote/index.json'
        MirrorUrl = ''

        # Offline mode: folder feed produced by scripts/export-offline-feed.ps1.
        # Repo-relative so the generated NuGet.config stays portable.
        OfflineFeedPath = 'offline-packages'

        # Optional: redirect the NuGet global package cache into the repo
        # (useful with small or roaming profiles), e.g. '.nuget-packages'.
        GlobalPackagesFolder = ''

        # $true silences NuGet's vulnerability-audit fetch (an outbound call that
        # fails soft as a warning). Recommended $true for offline mode.
        DisableAudit = $false
    }

    Conda = @{
        # Mirror mode: internal conda remote proxying conda-forge, e.g.
        # 'https://artifacts.corp.example/api/conda/conda-forge-remote'.
        # Empty = public conda-forge (the wizard warns when Mode = 'mirror').
        ChannelUrl = ''

        # Offline mode (optional): repo-relative folder with transferred
        # .conda/.tar.bz2 archives, e.g. 'offline-conda-pkgs'. Prepended to the
        # conda package-cache search path for the wizard's invocation only.
        OfflinePkgsDir = ''
    }

    Network = @{
        # Outbound proxy, e.g. 'http://proxy.corp.example:8080'. Applied to the
        # wizard's child processes and written as http_proxy into NuGet.config.
        ProxyUrl = ''

        # Comma-separated proxy bypass list, e.g. 'localhost,.corp.example'.
        NoProxy = ''

        # PEM bundle for corporate TLS interception. Used by conda per-invocation
        # (CONDA_SSL_VERIFY). NuGet trusts the Windows certificate store instead —
        # the corporate CA must be installed there via your approved channel.
        CaBundlePath = ''
    }

    Dotnet = @{
        # Sets DOTNET_CLI_TELEMETRY_OPTOUT=1 for the wizard's child processes.
        TelemetryOptOut = $true
    }
}
