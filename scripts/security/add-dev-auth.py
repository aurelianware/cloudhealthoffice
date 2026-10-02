"""Add the development ChoAuth block to a service's appsettings.Development.json."""
import json, sys, os, collections
KEY = "Q0hPLWRldmVsb3BtZW50LW9ubHktc2lnbmluZy1rZXktZG8tbm90LXVzZS0yMDI2"
path, client_id = sys.argv[1], sys.argv[2]
data = collections.OrderedDict()
if os.path.exists(path):
    with open(path) as f:
        txt = f.read().strip()
        if txt:
            data = json.loads(txt, object_pairs_hook=collections.OrderedDict)
data["ChoAuth"] = collections.OrderedDict([
    ("_comment", "Development-only trust. Symmetric keys are refused on any host other than Development/Testing. Deployed environments configure asymmetric keys from Key Vault (see docs/security/service-authentication.md)."),
    ("Audience", "cho-api"),
    ("Issuers", [
        {"Issuer": "cho-portal-dev", "SymmetricKey": KEY},
        {"Issuer": "cho-internal-dev", "SymmetricKey": KEY, "AllowServiceRole": True},
    ]),
    ("ServiceToken", {"Issuer": "cho-internal-dev", "ClientId": client_id, "SymmetricKey": KEY}),
])
with open(path, "w") as f:
    json.dump(data, f, indent=2)
    f.write("\n")
