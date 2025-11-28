/**
 * Generates Patient Access API specification artifacts.
 *
 * Outputs:
 *  - generated/infra/patient-access-api/capabilitystatement.json
 *  - generated/infra/patient-access-api/openapi.yaml
 */

import * as fs from 'fs';
import * as path from 'path';
import { CapabilityStatement, CapabilityStatementRestResource } from 'fhir/r4';
import { ProviderAccessApi } from '../src/fhir/provider-access-api';

const OUTPUT_DIR = path.join(process.cwd(), 'generated', 'infra', 'patient-access-api');

function ensureOutputDir(): void {
  if (!fs.existsSync(OUTPUT_DIR)) {
    fs.mkdirSync(OUTPUT_DIR, { recursive: true });
  }
}

function buildResource(type: string, profile: string): CapabilityStatementRestResource {
  return {
    type,
    profile,
    interaction: [
      { code: 'read' },
      { code: 'search-type' }
    ],
    conditionalRead: 'full-support',
    versioning: 'versioned-update'
  };
}

function generateCapabilityStatement(): CapabilityStatement {
  const api = new ProviderAccessApi();
  // Use ProviderAccessApi to ensure we stay aligned with existing mappings.
  // The instance is unused directly but ensures constructor side-effects (e.g. key generation)
  void api;

  const statement: CapabilityStatement = {
    resourceType: 'CapabilityStatement',
    status: 'active',
    date: new Date().toISOString(),
    kind: 'instance',
    software: {
      name: 'PatientAccessApi Logic App',
      version: '1.0.0'
    },
    implementation: {
      description: 'CMS-0057-F Patient Access API implemented as Azure Logic App',
      url: 'https://example.azurelogicapps.net/patient-access'
    },
    fhirVersion: '4.0.1',
    format: ['json'],
    rest: [
      {
        mode: 'server',
        documentation: 'FHIR R4 Patient Access API endpoints',
        security: {
          cors: true,
          service: [
            {
              coding: [
                {
                  system: 'http://terminology.hl7.org/CodeSystem/restful-security-service',
                  code: 'SMART-on-FHIR'
                }
              ],
              text: 'SMART on FHIR with Azure AD B2C'
            }
          ],
          description: 'OAuth2 / SMART-on-FHIR via Azure AD B2C. Required scopes: patient/*.read or user/*.read plus openid profile.'
        },
        resource: [
          buildResource('Patient', 'http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient'),
          buildResource('Coverage', 'http://hl7.org/fhir/us/core/StructureDefinition/us-core-coverage'),
          buildResource('ExplanationOfBenefit', 'http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-claim'),
          buildResource('Claim', 'http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-claim')
        ]
      }
    ]
  };

  return statement;
}

function buildOpenApiYaml(): string {
  const yaml = `openapi: 3.0.3
info:
  title: CMS-0057-F Patient Access API
  version: 1.0.0
  description: FHIR R4 Patient Access API served by Azure Logic App
servers:
  - url: https://example.azurelogicapps.net
paths:
  /Patient:
    get:
      operationId: ListPatients
      summary: Search for Patient resources
      security:
        - smartOnFhir:
            - patient/*.read
            - user/*.read
            - openid
            - profile
      parameters:
        - name: _id
          in: query
          schema:
            type: string
        - name: identifier
          in: query
          schema:
            type: string
        - name: _lastUpdated
          in: query
          schema:
            type: string
            format: date-time
        - name: _count
          in: query
          schema:
            type: integer
            minimum: 1
            maximum: 200
      responses:
        '200':
          description: Successful response
  /Patient/{id}:
    get:
      operationId: ReadPatient
      summary: Read a Patient resource
      security:
        - smartOnFhir:
            - patient/*.read
            - user/*.read
            - openid
            - profile
      parameters:
        - name: id
          in: path
          required: true
          schema:
            type: string
      responses:
        '200':
          description: Successful response
  /Coverage:
    get:
      operationId: ListCoverage
      summary: Search for Coverage resources
      security:
        - smartOnFhir:
            - patient/*.read
            - user/*.read
            - openid
            - profile
      parameters:
        - name: patient
          in: query
          schema:
            type: string
        - name: _lastUpdated
          in: query
          schema:
            type: string
            format: date-time
        - name: _count
          in: query
          schema:
            type: integer
            minimum: 1
            maximum: 200
      responses:
        '200':
          description: Successful response
  /ExplanationOfBenefit:
    get:
      operationId: ListExplanationOfBenefit
      summary: Search for ExplanationOfBenefit resources
      security:
        - smartOnFhir:
            - patient/*.read
            - user/*.read
            - openid
            - profile
      parameters:
        - name: patient
          in: query
          schema:
            type: string
        - name: _lastUpdated
          in: query
          schema:
            type: string
            format: date-time
        - name: _count
          in: query
          schema:
            type: integer
            minimum: 1
            maximum: 200
      responses:
        '200':
          description: Successful response
  /Claim:
    get:
      operationId: ListClaims
      summary: Search for Claim resources
      security:
        - smartOnFhir:
            - patient/*.read
            - user/*.read
            - openid
            - profile
      parameters:
        - name: patient
          in: query
          schema:
            type: string
        - name: _lastUpdated
          in: query
          schema:
            type: string
            format: date-time
        - name: _count
          in: query
          schema:
            type: integer
            minimum: 1
            maximum: 200
      responses:
        '200':
          description: Successful response
components:
  securitySchemes:
    smartOnFhir:
      type: oauth2
      flows:
        authorizationCode:
          authorizationUrl: https://{tenant}/oauth2/v2.0/authorize
          tokenUrl: https://{tenant}/oauth2/v2.0/token
          scopes:
            patient/*.read: Read patient-scoped FHIR data
            user/*.read: Read user-scoped FHIR data
            openid: OpenID Connect
            profile: User profile claims
`;
  return yaml;
}

export function generatePatientAccessSpecs(): void {
  ensureOutputDir();
  const capabilityStatement = generateCapabilityStatement();
  fs.writeFileSync(
    path.join(OUTPUT_DIR, 'capabilitystatement.json'),
    JSON.stringify(capabilityStatement, null, 2),
    'utf-8'
  );

  const openApiYaml = buildOpenApiYaml();
  fs.writeFileSync(path.join(OUTPUT_DIR, 'openapi.yaml'), openApiYaml, 'utf-8');
}

if (require.main === module) {
  generatePatientAccessSpecs();
}
