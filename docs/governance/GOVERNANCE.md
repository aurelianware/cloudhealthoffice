# Cloud Health Office Governance

This document outlines the governance structure, decision-making processes, and contributor pathways for the Cloud Health Office project.

## Table of Contents

- [Overview](#overview)
- [Governance Structure](#governance-structure)
- [Roles and Responsibilities](#roles-and-responsibilities)
- [Steering Committee](#steering-committee)
- [Decision Making](#decision-making)
- [Contribution Pathway](#contribution-pathway)
- [Code of Conduct Enforcement](#code-of-conduct-enforcement)
- [Amendments](#amendments)

## Overview

Cloud Health Office is a source-available, Azure-native multi-payer EDI platform for healthcare. Our governance model is designed to be transparent, inclusive, and meritocratic while ensuring the project maintains its focus on security, HIPAA compliance, and healthcare interoperability.

### Core Principles

1. **Transparency**: All decisions, discussions, and processes are conducted in public forums
2. **Meritocracy**: Advancement is based on contributions and demonstrated commitment
3. **Inclusivity**: All contributors are welcome regardless of background or affiliation
4. **Security First**: Healthcare compliance and security concerns take precedence
5. **Consensus Seeking**: We strive for consensus while accepting that not all decisions require unanimous agreement

## Governance Structure

```
┌─────────────────────────────────────────────────────────────┐
│                    Steering Committee                        │
│              (Strategic direction, final decisions)          │
└─────────────────────────────────────────────────────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                     ▼                     ▼
┌───────────────┐    ┌───────────────┐    ┌───────────────┐
│  Maintainers  │    │  Maintainers  │    │  Maintainers  │
│   (Core)      │    │   (Security)  │    │   (Docs)      │
└───────────────┘    └───────────────┘    └───────────────┘
        │                     │                     │
        └─────────────────────┼─────────────────────┘
                              ▼
                    ┌───────────────┐
                    │  Contributors │
                    └───────────────┘
                              │
                              ▼
                    ┌───────────────┐
                    │   Community   │
                    └───────────────┘
```

## Roles and Responsibilities

### Community Members

Anyone who interacts with the project. Community members can:
- Use the software
- Report bugs and request features
- Participate in discussions
- Contribute documentation improvements

### Contributors

Community members who have contributed to the project. Contributors can:
- Submit pull requests
- Review code (non-binding)
- Participate in issue triage
- Help answer community questions

**Requirements to become a Contributor:**
- At least one merged pull request
- Agreement to the [Code of Conduct](CODE_OF_CONDUCT.md)
- Signed DCO (Developer Certificate of Origin) on all commits

### Maintainers

Contributors who have demonstrated sustained commitment and expertise. Maintainers can:
- Merge pull requests
- Manage issues and projects
- Participate in release planning
- Represent the project in external forums
- Vote on project decisions

**Requirements to become a Maintainer:**
- Active contributor for at least 6 months
- At least 10 merged pull requests or equivalent contributions
- Demonstrated understanding of project architecture and goals
- Nomination by existing Maintainer and approval by Steering Committee
- Commitment to ongoing participation (minimum 4 hours/month)

**Maintainer Areas:**
- **Core**: Argo Workflows on AKS, infrastructure, API development
- **Security**: HIPAA compliance, security reviews, vulnerability management
- **Documentation**: User guides, API docs, architecture documentation
- **DevOps**: CI/CD, deployment automation, monitoring

### Steering Committee

The Steering Committee provides strategic direction and makes final decisions on project governance, disputes, and major architectural changes.

## Steering Committee

### Composition

The Steering Committee consists of 5-7 members:
- At least 3 seats reserved for active Maintainers
- At least 1 seat reserved for community/end-user representation
- Remaining seats open to any qualified nominee

### Responsibilities

1. **Strategic Direction**: Set long-term project vision and roadmap priorities
2. **Governance**: Maintain and update governance policies
3. **Dispute Resolution**: Final arbiter for technical and community disputes
4. **Budget Oversight**: Manage project finances (if applicable)
5. **Legal**: Oversee licensing, trademarks, and legal matters
6. **Security Policy**: Approve security policies and incident responses
7. **Release Authority**: Approve major releases and breaking changes

### Election Process

#### Timeline

Elections are held annually in Q1 (January-February):

| Week | Activity |
|------|----------|
| Week 1 | Call for nominations opens |
| Week 2 | Nomination period continues |
| Week 3 | Nominees confirmed, campaigning begins |
| Week 4 | Voting period (7 days) |
| Week 5 | Results announced, transition begins |

#### Eligibility

**To vote:**
- Must be a Contributor or Maintainer at the time voting opens
- Must have at least one contribution in the past 12 months
- Must not be under any Code of Conduct sanctions

**To run for Steering Committee:**
- Must be a Maintainer at the time of nomination, OR
- Must be a Contributor with at least 6 months of active participation
- Must have at least 5 contributions in the past 12 months
- Must not be under any Code of Conduct sanctions
- Must commit to serving a full 2-year term

#### Nomination Process

1. **Self-Nomination**: Candidates may nominate themselves
2. **Peer Nomination**: Any eligible voter may nominate another eligible person (nominee must accept)
3. **Nomination Requirements**:
   - Brief candidate statement (500 words max)
   - Summary of contributions to the project
   - Vision statement for project direction
   - Disclosure of any potential conflicts of interest

#### Voting

1. **Method**: Ranked-choice voting using Condorcet method
2. **Platform**: [Helios Voting](https://heliosvoting.org/) or equivalent secure, verifiable system
3. **Quorum**: At least 25% of eligible voters must participate
4. **Results**: Determined using Schulze method for multi-winner elections

#### Terms and Limits

- **Term Length**: 2 years
- **Term Limit**: Maximum of 3 consecutive terms (6 years)
- **Staggered Terms**: Half the committee is elected each year to ensure continuity

#### Vacancies

If a Steering Committee seat becomes vacant:
- If more than 6 months remain in the term, a special election is held within 30 days
- If less than 6 months remain, the committee may appoint an interim member
- Interim appointments require majority committee approval

#### Removal

A Steering Committee member may be removed by:
- Voluntary resignation (30-day notice requested)
- Two-thirds vote of the Steering Committee for cause
- Unanimous vote of the other committee members for Code of Conduct violations
- Automatic removal for 3 consecutive missed meetings without notice

## Decision Making

### Lazy Consensus

Most decisions are made through lazy consensus:
1. A proposal is made (pull request, issue, or discussion)
2. A reasonable waiting period is observed (typically 72 hours for minor changes, 1 week for significant changes)
3. If no objections are raised, the proposal is accepted
4. Silence is treated as implicit agreement

### Voting

When consensus cannot be reached, decisions are made by vote:

| Decision Type | Required Vote | Voting Body |
|--------------|---------------|-------------|
| Code changes (normal) | 1 Maintainer approval | Maintainers |
| Code changes (security-sensitive) | 2 Maintainer approvals | Maintainers + Security |
| New Maintainer | Majority | Steering Committee |
| Governance changes | Two-thirds | Steering Committee |
| Major architectural changes | Two-thirds | Steering Committee |
| Breaking changes | Two-thirds | Steering Committee |
| Code of Conduct enforcement | Majority | Steering Committee |
| License changes | Unanimous | Steering Committee |

### RFC Process

Major changes require a Request for Comments (RFC):

1. **Draft**: Author creates RFC document in `docs/rfcs/` directory
2. **Discussion**: 2-week minimum discussion period
3. **Revision**: Author incorporates feedback
4. **Final Comment Period**: 1-week final review
5. **Decision**: Steering Committee votes to accept, reject, or postpone

## Contribution Pathway

```
Community Member
      │
      ▼ (first contribution)
Contributor
      │
      ▼ (6 months + 10 contributions + nomination)
Maintainer
      │
      ▼ (election)
Steering Committee
```

### Recognition

Contributors are recognized through:
- Listing in CONTRIBUTORS.md
- Release notes acknowledgment
- Annual contributor appreciation
- Reference letters upon request

## Code of Conduct Enforcement

See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for the full Code of Conduct.

### Enforcement Process

1. **Report**: Issues reported to conduct@cloudhealthoffice.dev
2. **Review**: Steering Committee reviews within 48 hours
3. **Investigation**: Fact-finding with all parties (confidential)
4. **Decision**: Committee determines appropriate response
5. **Action**: Enforcement action taken
6. **Appeal**: One appeal permitted within 14 days

### Recusal

Committee members must recuse themselves from enforcement decisions where they have a conflict of interest or are personally involved in the incident.

## Amendments

### Process

1. Amendments proposed via pull request to GOVERNANCE.md
2. Two-week discussion period
3. Two-thirds vote of Steering Committee required
4. Changes take effect 30 days after approval

### Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2025-01-01 | Initial governance document |

---

## Contact

- **General Questions**: Open a [GitHub Discussion](https://github.com/aurelianware/cloudhealthoffice/discussions)
- **Code of Conduct**: conduct@cloudhealthoffice.dev
- **Security Issues**: See [SECURITY.md](SECURITY.md)
- **Steering Committee**: steering@cloudhealthoffice.dev

---

*This governance document is licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).*
