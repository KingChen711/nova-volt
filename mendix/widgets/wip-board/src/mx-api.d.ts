// Khai báo phần API sử dụng từ tài liệu Mendix 11 Client API, module mx-api/data.
// Runtime cung cấp implementation; npm mendix11.8 chưa kèm declaration này.
declare module "mx-api/data" {
    export function retrieveByEntity(params: {
        entity: string;
        filter?: { offset?: number; limit?: number; sort?: Array<[string, "asc" | "desc"]> };
    }): Promise<Array<{ getGuid(): string; get(attribute: string): unknown }>>;
}
