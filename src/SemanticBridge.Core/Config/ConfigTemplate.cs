namespace SemanticBridge.Core.Config;

public static class ConfigTemplate
{
    public const string Text = """
        # Semantic Bridge for Power BI - configuration
        #
        # Precedence, highest first: command-line flag > environment variable > this file > cloud preset.
        # Environment variables: SBRIDGE_CONFIG, SBRIDGE_PROFILE, SBRIDGE_TENANT_ID, SBRIDGE_CLIENT_ID,
        # SBRIDGE_CLIENT_SECRET, SBRIDGE_CLOUD. With none of this file's profiles needed, the last four are enough.
        #
        # WARNING: write capabilities (allow_write: true) change the LIVE semantic model and affect every
        # report built on it. Keep allow_write false. If you need writes, use a separate profile with a
        # separate service principal that has write rights, and enable it only when needed.

        # Profile used when --profile / SBRIDGE_PROFILE is not given.
        default_profile: default

        profiles:
          default:
            # global | china | custom. "custom" needs all four endpoint keys below.
            cloud: global

            tenant_id: "00000000-0000-0000-0000-000000000000"
            client_id: "00000000-0000-0000-0000-000000000000"

            # Prefer an environment variable over a plaintext secret. SBRIDGE_CLIENT_SECRET is always read;
            # to read a differently named variable, name it here:
            # client_secret_env: MY_SECRET_VARIABLE
            # client_secret: "plaintext secrets in files are discouraged"

            # Optional endpoint overrides (defaults come from the cloud preset):
            # authority_host: https://login.microsoftonline.com
            # token_scope: https://analysis.windows.net/powerbi/api/.default
            # rest_base_url: https://api.powerbi.com/v1.0/myorg
            # xmla_base: powerbi://api.powerbi.com/v1.0/myorg/{workspace}

            # Workspace name or ID used when -w is not given.
            # default_workspace: My Workspace

            # Writes are off unless this is true. See the warning at the top of this file.
            allow_write: false

            # Row limits. max_rows is the default per query; max_rows_ceiling (1..100000) is the hard cap.
            max_rows: 1000
            max_rows_ceiling: 100000
            # Seconds, clamped to 1..600.
            query_timeout_seconds: 120
            # auto | xmla | rest. auto tries XMLA and falls back to REST only when the workspace has no XMLA endpoint.
            default_via: auto
        """ + "\n";
}
