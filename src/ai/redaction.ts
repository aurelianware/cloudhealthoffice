/**
 * Simple PHI redaction stub – extend logic for real PHI fields/regex.
 */
export function isPHI(val: string): boolean {
  // Example: redact if value looks like SSN or MRN; use real patterns!
  return /^\d{3}-\d{2}-\d{4}$/.test(val);
}

export function redactPHI<T>(obj: T): T {
  // Replace string fields that are PHI
  const clone: any = { ...obj };
  for (const k in clone) {
    if (typeof clone[k] === "string" && isPHI(clone[k])) clone[k] = "***REDACTED***";
  }
  return clone as T;
}