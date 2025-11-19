/**
 * Handlebars Template Helpers
 * Custom helper functions for template rendering
 */

import * as Handlebars from 'handlebars';

export function registerHelpers(): void {
  // String helpers
  Handlebars.registerHelper('uppercase', (str: string) => {
    return str ? str.toUpperCase() : '';
  });

  Handlebars.registerHelper('lowercase', (str: string) => {
    return str ? str.toLowerCase() : '';
  });

  Handlebars.registerHelper('camelCase', (str: string) => {
    if (!str) return '';
    return str
      .replace(/[-_\s]+(.)?/g, (_, c) => (c ? c.toUpperCase() : ''))
      .replace(/^(.)/, (c) => c.toLowerCase());
  });

  Handlebars.registerHelper('pascalCase', (str: string) => {
    if (!str) return '';
    return str
      .replace(/[-_\s]+(.)?/g, (_, c) => (c ? c.toUpperCase() : ''))
      .replace(/^(.)/, (c) => c.toUpperCase());
  });

  Handlebars.registerHelper('kebabCase', (str: string) => {
    if (!str) return '';
    return str
      .replace(/([a-z])([A-Z])/g, '$1-$2')
      .replace(/[\s_]+/g, '-')
      .toLowerCase();
  });

  Handlebars.registerHelper('snakeCase', (str: string) => {
    if (!str) return '';
    return str
      .replace(/([a-z])([A-Z])/g, '$1_$2')
      .replace(/[\s-]+/g, '_')
      .toLowerCase();
  });

  // Array helpers
  Handlebars.registerHelper('join', function(arr: any[], separator: string) {
    if (!Array.isArray(arr)) return '';
    return arr.join(separator || ', ');
  });

  Handlebars.registerHelper('first', function(arr: any[]) {
    if (!Array.isArray(arr) || arr.length === 0) return undefined;
    return arr[0];
  });

  Handlebars.registerHelper('last', function(arr: any[]) {
    if (!Array.isArray(arr) || arr.length === 0) return undefined;
    return arr[arr.length - 1];
  });

  Handlebars.registerHelper('length', function(arr: any[]) {
    if (!Array.isArray(arr)) return 0;
    return arr.length;
  });

  Handlebars.registerHelper('contains', function(arr: any[], val: any) {
    if (!Array.isArray(arr)) return false;
    return arr.includes(val);
  });

  // Conditional helpers
  Handlebars.registerHelper('eq', function(a: any, b: any) {
    return a === b;
  });

  Handlebars.registerHelper('ne', function(a: any, b: any) {
    return a !== b;
  });

  Handlebars.registerHelper('lt', function(a: any, b: any) {
    return a < b;
  });

  Handlebars.registerHelper('gt', function(a: any, b: any) {
    return a > b;
  });

  Handlebars.registerHelper('lte', function(a: any, b: any) {
    return a <= b;
  });

  Handlebars.registerHelper('gte', function(a: any, b: any) {
    return a >= b;
  });

  Handlebars.registerHelper('and', function(...args: any[]) {
    // Remove the last argument (Handlebars options object)
    const params = args.slice(0, -1);
    return params.every(Boolean);
  });

  Handlebars.registerHelper('or', function(...args: any[]) {
    // Remove the last argument (Handlebars options object)
    const params = args.slice(0, -1);
    return params.some(Boolean);
  });

  Handlebars.registerHelper('not', function(value: any) {
    return !value;
  });

  // JSON helpers
  Handlebars.registerHelper('json', function(obj: any) {
    return new Handlebars.SafeString(JSON.stringify(obj, null, 2));
  });

  Handlebars.registerHelper('jsonInline', function(obj: any) {
    return new Handlebars.SafeString(JSON.stringify(obj));
  });

  Handlebars.registerHelper('jsonEscape', function(str: string) {
    if (!str) return '';
    return str
      .replace(/\\/g, '\\\\')
      .replace(/"/g, '\\"')
      .replace(/\n/g, '\\n')
      .replace(/\r/g, '\\r')
      .replace(/\t/g, '\\t');
  });

  // Math helpers
  Handlebars.registerHelper('add', function(a: number, b: number) {
    return a + b;
  });

  Handlebars.registerHelper('subtract', function(a: number, b: number) {
    return a - b;
  });

  Handlebars.registerHelper('multiply', function(a: number, b: number) {
    return a * b;
  });

  Handlebars.registerHelper('divide', function(a: number, b: number) {
    if (b === 0) return 0;
    return a / b;
  });

  // Date helpers
  Handlebars.registerHelper('now', function() {
    return new Date().toISOString();
  });

  Handlebars.registerHelper('formatDate', function(date: string | Date, format?: string) {
    const d = typeof date === 'string' ? new Date(date) : date;
    if (format === 'iso') return d.toISOString();
    if (format === 'date') return d.toISOString().split('T')[0];
    return d.toLocaleDateString();
  });

  // Type helpers
  Handlebars.registerHelper('typeof', function(value: any) {
    return typeof value;
  });

  Handlebars.registerHelper('isArray', function(value: any) {
    return Array.isArray(value);
  });

  Handlebars.registerHelper('isObject', function(value: any) {
    return typeof value === 'object' && value !== null && !Array.isArray(value);
  });

  Handlebars.registerHelper('isString', function(value: any) {
    return typeof value === 'string';
  });

  Handlebars.registerHelper('isNumber', function(value: any) {
    return typeof value === 'number';
  });

  Handlebars.registerHelper('isBoolean', function(value: any) {
    return typeof value === 'boolean';
  });

  // Utility helpers
  Handlebars.registerHelper('default', function(value: any, defaultValue: any) {
    return value !== undefined && value !== null ? value : defaultValue;
  });

  Handlebars.registerHelper('coalesce', function(...args: any[]) {
    // Remove the last argument (Handlebars options object)
    const params = args.slice(0, -1);
    for (const param of params) {
      if (param !== undefined && param !== null) {
        return param;
      }
    }
    return undefined;
  });

  Handlebars.registerHelper('substring', function(str: string, start: number, end?: number) {
    if (!str) return '';
    return str.substring(start, end);
  });

  Handlebars.registerHelper('replace', function(str: string, search: string, replacement: string) {
    if (!str) return '';
    return str.replace(new RegExp(search, 'g'), replacement);
  });

  Handlebars.registerHelper('trim', function(str: string) {
    return str ? str.trim() : '';
  });

  Handlebars.registerHelper('split', function(str: string, delimiter: string) {
    if (!str) return [];
    return str.split(delimiter);
  });

  Handlebars.registerHelper('indent', function(str: string, spaces: number) {
    if (!str) return '';
    const indent = ' '.repeat(spaces || 2);
    return str.split('\n').map(line => indent + line).join('\n');
  });

  // Iteration helpers
  Handlebars.registerHelper('times', function(n: number, options: any) {
    let result = '';
    for (let i = 0; i < n; i++) {
      result += options.fn({ index: i, count: n });
    }
    return result;
  });

  Handlebars.registerHelper('range', function(start: number, end: number, options: any) {
    let result = '';
    for (let i = start; i < end; i++) {
      result += options.fn({ value: i, index: i - start });
    }
    return result;
  });

  // Azure-specific helpers
  Handlebars.registerHelper('resourceName', function(prefix: string, type: string, suffix?: string) {
    const parts = [prefix, type];
    if (suffix) parts.push(suffix);
    return parts.join('-');
  });

  Handlebars.registerHelper('storageAccountName', function(prefix: string) {
    // Azure storage account names: lowercase letters and numbers only, 3-24 chars
    return prefix.toLowerCase().replace(/[^a-z0-9]/g, '').substring(0, 24);
  });

  Handlebars.registerHelper('keyVaultName', function(prefix: string, env?: string) {
    // Azure Key Vault names: alphanumeric and hyphens only, 3-24 chars
    const parts = [prefix];
    if (env) parts.push(env);
    parts.push('kv');
    return parts.join('-').toLowerCase().substring(0, 24);
  });
}

export function unregisterHelpers(): void {
  // Unregister all helpers (useful for testing)
  Handlebars.unregisterHelper('uppercase');
  Handlebars.unregisterHelper('lowercase');
  Handlebars.unregisterHelper('camelCase');
  Handlebars.unregisterHelper('pascalCase');
  Handlebars.unregisterHelper('kebabCase');
  Handlebars.unregisterHelper('snakeCase');
  Handlebars.unregisterHelper('join');
  Handlebars.unregisterHelper('first');
  Handlebars.unregisterHelper('last');
  Handlebars.unregisterHelper('length');
  Handlebars.unregisterHelper('contains');
  Handlebars.unregisterHelper('eq');
  Handlebars.unregisterHelper('ne');
  Handlebars.unregisterHelper('lt');
  Handlebars.unregisterHelper('gt');
  Handlebars.unregisterHelper('lte');
  Handlebars.unregisterHelper('gte');
  Handlebars.unregisterHelper('and');
  Handlebars.unregisterHelper('or');
  Handlebars.unregisterHelper('not');
  Handlebars.unregisterHelper('json');
  Handlebars.unregisterHelper('jsonInline');
  Handlebars.unregisterHelper('jsonEscape');
}
