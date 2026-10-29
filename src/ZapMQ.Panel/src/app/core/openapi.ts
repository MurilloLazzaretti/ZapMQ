import { ApiDocs, EndpointCommand } from './models';

/** One parameter of an operation, as OpenAPI 3 or Swagger 2 says it. */
export interface EndpointParameter {
  name: string;
  in: string;
  required: boolean;
  type: string;
  description: string;
}

/** One endpoint of an API: what the OpenAPI document says of it and what the map says it runs. */
export interface Endpoint {
  key: string;
  method: string;
  route: string;
  group: string;
  summary: string;
  description: string;
  deprecated: boolean;
  parameters: EndpointParameter[];
  /** The body, as a tree of fields in text; empty when there is none. */
  body: string;
  responses: { status: string; description: string; schema: string }[];
  commands: EndpointCommand[];
  found: { name: string; kind: string | null }[];
  handler: string;
  /** Which of the two said it. */
  documented: boolean;
  mapped: boolean;
}

const METHODS = ['get', 'post', 'put', 'patch', 'delete', 'head', 'options'];

/** What tells the same endpoint in both: the verb and the route, without case and without the names of its parameters. */
export function endpointKey(method: string, route: string): string {
  let path = route.trim().toLowerCase().replace(/\{[^}]*\}/g, '{}').replace(/\/+$/, '');
  if (!path.startsWith('/')) {
    path = '/' + path;
  }
  return `${method.trim().toUpperCase()} ${path || '/'}`;
}

/** The endpoints of an API, from its OpenAPI document and its map, joined; the ones of the document first, in its order. */
export function readEndpoints(docs: ApiDocs): Endpoint[] {
  const all = new Map<string, Endpoint>();
  const document = docs.OpenApi;
  if (document?.['paths']) {
    for (const [route, item] of Object.entries<any>(document['paths'])) {
      for (const method of METHODS) {
        const operation = item?.[method];
        if (!operation) {
          continue;
        }
        const key = endpointKey(method, route);
        all.set(key, {
          key,
          method: method.toUpperCase(),
          route,
          group: operation.tags?.[0] ?? firstSegment(route),
          summary: operation.summary ?? '',
          description: operation.description ?? '',
          deprecated: !!operation.deprecated,
          parameters: [...(item.parameters ?? []), ...(operation.parameters ?? [])].map((parameter: any) => resolve(document, parameter)).filter((parameter: any) => parameter?.in !== 'body').map((parameter: any) => ({
            name: parameter.name ?? '',
            in: parameter.in ?? '',
            required: !!parameter.required,
            type: parameter.schema ? typeOf(document, parameter.schema) : (parameter.type ?? ''),
            description: parameter.description ?? '',
          })),
          body: bodyOf(document, operation),
          responses: Object.entries<any>(operation.responses ?? {}).map(([status, response]) => {
            const resolved = resolve(document, response);
            const schema = resolved?.content ? firstContent(resolved.content)?.schema : resolved?.schema;
            return { status, description: resolved?.description ?? '', schema: schema ? tree(document, schema) : '' };
          }),
          commands: [],
          found: [],
          handler: operation.operationId ?? '',
          documented: true,
          mapped: false,
        });
      }
    }
  }
  for (const mapped of docs.Map?.endpoints ?? []) {
    const key = endpointKey(mapped.method, mapped.route);
    const known = all.get(key);
    if (known) {
      known.commands = mapped.commands ?? [];
      known.found = mapped.found ?? [];
      known.handler = mapped.handler || known.handler;
      known.mapped = true;
    } else {
      all.set(key, {
        key,
        method: mapped.method.toUpperCase(),
        route: mapped.route,
        group: mapped.handler?.split('.')[0]?.replace(/Controller$/, '') || firstSegment(mapped.route),
        summary: '',
        description: '',
        deprecated: false,
        parameters: [],
        body: '',
        responses: [],
        commands: mapped.commands ?? [],
        found: mapped.found ?? [],
        handler: mapped.handler ?? '',
        documented: false,
        mapped: true,
      });
    }
  }
  return [...all.values()];
}

function firstSegment(route: string): string {
  const parts = route.split('/').filter((part) => part && part.toLowerCase() !== 'api' && !part.startsWith('{'));
  return parts[1] ?? parts[0] ?? '/';
}

function firstContent(content: Record<string, any>): any {
  return content['application/json'] ?? content['text/json'] ?? Object.values(content)[0];
}

function bodyOf(document: Record<string, any>, operation: any): string {
  const body = resolve(document, operation.requestBody);
  if (body?.content) {
    const schema = firstContent(body.content)?.schema;
    return schema ? tree(document, schema) : '';
  }
  // Swagger 2: the body is a parameter.
  const parameter = (operation.parameters ?? []).map((item: any) => resolve(document, item)).find((item: any) => item?.in === 'body');
  return parameter?.schema ? tree(document, parameter.schema) : '';
}

/** What a $ref points to, in OpenAPI 3 (#/components/...) or Swagger 2 (#/definitions/...). */
function resolve(document: Record<string, any>, node: any, depth = 0): any {
  if (!node?.$ref || depth > 8) {
    return node;
  }
  const target = String(node.$ref)
    .replace(/^#\//, '')
    .split('/')
    .reduce((at: any, part: string) => at?.[part.replace(/~1/g, '/').replace(/~0/g, '~')], document);
  return resolve(document, target, depth + 1);
}

function refName(node: any): string {
  return node?.$ref ? String(node.$ref).split('/').pop()! : '';
}

/** A schema in a few words: string, integer, Material, Material[]. */
function typeOf(document: Record<string, any>, schema: any): string {
  if (!schema) {
    return '';
  }
  if (schema.$ref) {
    return refName(schema);
  }
  if (schema.type === 'array') {
    return typeOf(document, schema.items) + '[]';
  }
  const kind = Array.isArray(schema.type) ? schema.type.filter((item: string) => item !== 'null').join('|') : schema.type;
  return [kind || (schema.properties ? 'object' : schema.oneOf || schema.anyOf ? 'um de vários' : 'qualquer'), schema.format ? `(${schema.format})` : '', schema.nullable ? ' | null' : ''].join('');
}

/** A schema as a tree of fields in text, a few levels deep, each named type opened once. */
function tree(document: Record<string, any>, schema: any, depth = 0, seen = new Set<string>(), indent = ''): string {
  const name = refName(schema);
  const resolved = resolve(document, schema);
  if (!resolved) {
    return '';
  }
  if (resolved.type === 'array') {
    return tree(document, resolved.items, depth, seen, indent) + '[]';
  }
  const properties: Record<string, any> | undefined = resolved.properties;
  if (!properties || depth > 3 || (name && seen.has(name))) {
    return typeOf(document, schema);
  }
  const inner = new Set(seen);
  if (name) {
    inner.add(name);
  }
  const required = new Set<string>(resolved.required ?? []);
  const lines = Object.entries(properties).map(([field, value]) => {
    const child = tree(document, value, depth + 1, inner, indent + '  ');
    return `${indent}  ${field}${required.has(field) ? '' : '?'}: ${child}`;
  });
  return `${name ? name + ' ' : ''}{\n${lines.join('\n')}\n${indent}}`;
}
