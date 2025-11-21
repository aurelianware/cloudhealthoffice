# Migration Guide: HIPAA Attachments → Cloud Health Office

This guide provides comprehensive instructions for organizations transitioning from the legacy "HIPAA Attachments" branding to the new "Cloud Health Office" platform branding.

## Overview

**What's Changing:**
- Repository branding from "HIPAA Attachments" to "Cloud Health Office"
- Enhanced positioning as a multi-tenant SaaS platform
- Updated documentation reflecting expanded capabilities
- Clearer separation from Availity (independent integration platform)
- Addition of Azure Marketplace roadmap and commercial SaaS features

**What's NOT Changing:**
- Core technical implementation and architecture
- Azure resource names and deployment structure
- API contracts and EDI transaction formats
- Security controls and HIPAA compliance
- Configuration schema and workflow definitions
- All existing functionality remains intact

## For Existing Users

### No Action Required

If you have an existing deployment of the platform (previously called "HIPAA Attachments"), **no immediate action is required**. Your deployment will continue to function exactly as before.

The rebranding is:
- ✅ **Backward compatible** - All existing configurations work
- ✅ **Non-breaking** - No API or schema changes
- ✅ **Optional adoption** - Update documentation at your convenience
- ✅ **Zero downtime** - No service interruption

### Recommended Updates (Optional)

While not required, we recommend updating your internal documentation and references:

#### 1. Update Internal Documentation

Replace references in your documentation:
- **Old**: "HIPAA Attachments Processing System"
- **New**: "Cloud Health Office Platform"

Example locations:
- Internal wiki pages and runbooks
- Architecture diagrams and presentations
- Training materials and onboarding guides
- Support documentation and troubleshooting guides

#### 2. Update Azure Resource Tags (Optional)

If you use Azure resource tags for organization, consider updating:

```bash
# Example: Update resource group tags
az group update \
  --name "payer-attachments-prod-rg" \
  --tags "Platform=Cloud Health Office" "Version=2024.11" "Environment=Production"

# Example: Update Logic App tags
az webapp update \
  --resource-group "payer-attachments-prod-rg" \
  --name "hipaa-attachments-prod-la" \
  --set tags.Platform="Cloud Health Office"
```

**Note**: This is purely organizational and does not affect functionality.

#### 3. Update Monitoring Dashboards

If you have custom Application Insights dashboards or alerts with "HIPAA Attachments" in the title, consider updating for clarity:

- Dashboard names
- Alert rule names and descriptions
- Workbook titles and descriptions

#### 4. Update Git Remotes (If Self-Hosted)

If you forked or cloned the repository and maintain your own remote:

```bash
# Check current remote
git remote -v

# If pointing to old references, no action needed
# The GitHub repository path remains: aurelianware/hipaa-attachments

# Note: Repository name in GitHub may be updated in the future
# We will provide advance notice if repository URL changes
```

## For New Users

### Getting Started with Cloud Health Office

New implementations should use the updated branding and documentation:

1. **Clone the Repository**:
   ```bash
   git clone https://github.com/aurelianware/hipaa-attachments.git
   cd hipaa-attachments
   ```

2. **Follow the Quick Implementation Guide** in the README.md

3. **Use Updated Documentation**:
   - All documentation now references "Cloud Health Office"
   - Security features are clearly documented with deployment guides
   - Azure Marketplace roadmap is included for future SaaS options

## Terminology Changes

### Platform Name

| Old Term | New Term | Notes |
|----------|----------|-------|
| HIPAA Attachments | Cloud Health Office | Official platform name |
| HIPAA Attachments Processing System | Cloud Health Office Platform | Full platform name |
| Attachment processing | Multi-payer EDI integration | Expanded scope |

### Capability Descriptions

| Old Description | New Description | Reason |
|----------------|-----------------|--------|
| "275 attachment processing" | "Multi-payer EDI processing (Claims, Eligibility, Attachments, Authorizations, Appeals)" | Reflects full platform capabilities |
| "Availity integration" | "Clearinghouse integration (Availity, Change Healthcare, etc.)" | Platform-agnostic positioning |
| "QNXT backend" | "Backend-agnostic (QNXT, FacetsRx, TriZetto, Epic, Cerner, custom)" | Works with any claims system |
| "Payer onboarding" | "Zero-code tenant onboarding" | SaaS multi-tenant terminology |

### Security Features

Now explicitly documented and deployed:
- **Azure Key Vault Integration** (Premium SKU with HSM)
- **Private Endpoints & Network Isolation** (VNet, Private DNS)
- **PHI Masking in Application Insights** (Data Collection Rules)
- **Automated Secret Rotation** (Key Vault policies)
- **Data Lifecycle Management** (Hot → Cool → Archive → Delete)

These features were previously implemented but not prominently documented.

## Repository References

### GitHub Repository

The GitHub repository remains at:
```
https://github.com/aurelianware/hipaa-attachments
```

**Future Update**: We may rename the repository to `cloud-health-office` in the future. If this happens:
- GitHub will automatically redirect old URLs
- We will provide 90 days advance notice
- All git clones and references will continue to work
- Detailed migration steps will be provided

### Package References

If you reference this platform in your package manifests:

**package.json** (Node.js):
```json
{
  "name": "your-integration",
  "dependencies": {
    "@aurelianware/cloud-health-office": "^1.0.0"
  }
}
```

**composer.json** (PHP):
```json
{
  "require": {
    "aurelianware/cloud-health-office": "^1.0"
  }
}
```

## Support and Questions

### Getting Help

If you have questions about the migration or rebranding:

1. **Documentation**: Review the updated README.md and architecture docs
2. **GitHub Discussions**: Ask questions in the [Discussions forum](https://github.com/aurelianware/hipaa-attachments/discussions)
3. **GitHub Issues**: Report problems with [GitHub Issues](https://github.com/aurelianware/hipaa-attachments/issues)
4. **Enterprise Support**: Contact sales@aurelianware.com for commercial support

### Common Questions

**Q: Do I need to redeploy my Logic Apps?**
A: No, existing deployments continue to work without changes.

**Q: Will my configuration files still work?**
A: Yes, all configuration schemas are backward compatible.

**Q: Are there breaking API changes?**
A: No, all APIs and EDI transaction formats remain unchanged.

**Q: Do I need to update my Bicep templates?**
A: No, existing infrastructure templates work as-is. You can update comments/documentation at your convenience.

**Q: What about my Azure resource names?**
A: Keep your existing names. No need to rename resources (e.g., `hipaa-attachments-prod-la` can stay).

**Q: When will the GitHub repository be renamed?**
A: Not immediately. If we rename in the future, we'll provide 90 days notice and automatic redirects.

**Q: How do I know what version I'm running?**
A: Check your deployment's Application Insights or Logic App tags. Versions after November 2024 include the Cloud Health Office branding.

**Q: Is this a new product?**
A: No, it's the same platform with updated branding to reflect its expanded SaaS capabilities.

**Q: What's the connection to Availity?**
A: Cloud Health Office integrates with Availity (and other clearinghouses), but is an independent platform. This is now clearly stated in documentation with appropriate disclaimers.

## Timeline

### November 2024
- ✅ Documentation rebranding complete
- ✅ README and architecture guides updated
- ✅ Security features explicitly documented
- ✅ Azure Marketplace roadmap published

### Q1 2025
- GitHub repository may be renamed to `cloud-health-office`
- Azure Marketplace preparation (listing assets, screenshots, videos)
- Customer portal development (for SaaS offering)

### Q2 2025
- Azure Marketplace transactable offer launch (target)
- Public SaaS offering with subscription plans
- Self-service customer onboarding portal

## Additional Resources

- **[README.md](README.md)** - Platform overview with Quick Implementation Guide
- **[ARCHITECTURE.md](ARCHITECTURE.md)** - Updated architecture documentation
- **[SECURITY-HARDENING.md](SECURITY-HARDENING.md)** - Complete security implementation guide
- **[DEPLOYMENT.md](DEPLOYMENT.md)** - Step-by-step deployment procedures
- **[ROADMAP.md](ROADMAP.md)** - Future enhancements and Azure Marketplace plans

## Disclaimer

**Platform Independence**: Cloud Health Office is an independent EDI integration platform. It supports connectivity with Availity, Change Healthcare, and other clearinghouses. This platform is not affiliated with, endorsed by, or sponsored by Availity, LLC or any other clearinghouse mentioned in this documentation.

---

**Last Updated**: November 2024  
**Version**: 1.0  
**Maintained By**: Aurelianware Cloud Health Office Team
