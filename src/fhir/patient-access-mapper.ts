/**
 * Patient Access API Mapper utilities
 *
 * Reuses existing ProviderAccessApi mapping and HIPAA redaction helpers to
 * build FHIR bundles for Patient Access API responses.
 */

import {
  Bundle,
  BundleEntry,
  Coverage,
  ExplanationOfBenefit,
  Claim
} from 'fhir/r4';
import {
  ProviderAccessApi,
  QnxtPatient,
  QnxtClaim
} from './provider-access-api';
import { redactPHI } from '../security/hipaaLogger';

const providerApi = new ProviderAccessApi();

function buildBundle(entries: BundleEntry[], selfLink: string): Bundle {
  return {
    resourceType: 'Bundle',
    type: 'searchset',
    total: entries.length,
    link: [{ relation: 'self', url: selfLink }],
    entry: entries
  };
}

export function patientsToBundle(patients: QnxtPatient[], selfLink: string): Bundle {
  const bundleEntries: BundleEntry[] = patients.map(patient => {
    const fhirPatient = providerApi.mapQnxtPatientToFhir(patient);
    return {
      fullUrl: `Patient/${fhirPatient.id}`,
      resource: providerApi.redactPhi(fhirPatient)
    };
  });

  return buildBundle(bundleEntries, selfLink);
}

export function coverageToBundle(patients: QnxtPatient[], selfLink: string): Bundle {
  const bundleEntries: BundleEntry[] = patients.map(patient => {
    const coverage: Coverage = {
      resourceType: 'Coverage',
      id: `${patient.memberId}-COV`,
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-coverage']
      },
      status: 'active',
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/v3-ActCode',
          code: 'SUBSCR'
        }]
      },
      beneficiary: {
        reference: `Patient/${patient.memberId}`
      },
      subscriberId: patient.memberId,
      payor: [{
        display: 'Cloud Health Office Plan'
      }]
    };

    return {
      fullUrl: `Coverage/${coverage.id}`,
      resource: redactPHI(coverage)
    };
  });

  return buildBundle(bundleEntries, selfLink);
}

export function claimsToBundle(claims: QnxtClaim[], selfLink: string): Bundle {
  const bundleEntries: BundleEntry[] = claims.map(claim => {
    const fhirClaim: Claim = providerApi.mapQnxtClaimToFhir(claim);
    return {
      fullUrl: `Claim/${fhirClaim.id}`,
      resource: providerApi.redactPhi(fhirClaim)
    };
  });

  return buildBundle(bundleEntries, selfLink);
}

export interface PaymentDocument {
  paymentId: string;
  claimId: string;
  memberId: string;
  paymentDate: string;
  totalPaid: number;
  status?: string;
}

export function paymentsToEobBundle(payments: PaymentDocument[], selfLink: string): Bundle {
  const entries: BundleEntry[] = payments.map(payment => {
    const eob: ExplanationOfBenefit = {
      resourceType: 'ExplanationOfBenefit',
      id: payment.paymentId,
      status: (payment.status as ExplanationOfBenefit['status']) || 'active',
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/claim-type',
          code: 'professional'
        }]
      },
      use: 'claim',
      patient: {
        reference: `Patient/${payment.memberId}`
      },
      created: payment.paymentDate,
      insurer: {
        display: 'Cloud Health Office Plan'
      },
      provider: {
        display: 'Rendering Provider'
      },
      outcome: 'complete',
      insurance: [
        {
          focal: true,
          coverage: {
            reference: `Coverage/${payment.memberId}-COV`
          }
        }
      ],
      payment: {
        amount: {
          value: payment.totalPaid,
          currency: 'USD'
        }
      },
      supportingInfo: [{
        sequence: 1,
        category: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/eob-information',
            code: 'clmrecv'
          }]
        },
        valueString: `Claim ${payment.claimId}`
      }]
    };

    return {
      fullUrl: `ExplanationOfBenefit/${eob.id}`,
      resource: providerApi.redactPhi(eob)
    };
  });

  return buildBundle(entries, selfLink);
}
