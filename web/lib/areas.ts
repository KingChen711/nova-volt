import type { LucideIcon } from "lucide-react";
import {
  Boxes,
  ClipboardCheck,
  Factory,
  LayoutDashboard,
  ListChecks,
  PackageOpen,
  Settings2,
  ShieldAlert,
  Siren,
  Truck,
} from "lucide-react";

export type AreaGroup = "Tổng quan" | "Vận hành" | "Chất lượng" | "Hậu cần" | "Quản trị";

/**
 * Khu của web, chia theo cách hệ thật chia app: mỗi khu một nhóm người dùng (scope v2, R13). Role là realm role
 * trong token Keycloak; mom-admin thấy mọi khu. `milestone` là mốc dựng màn thật của khu; `screens` là các màn dự kiến.
 */
export type Area = {
  slug: string;
  label: string;
  group: AreaGroup;
  description: string;
  roles: string[];
  icon: LucideIcon;
  milestone: string;
  screens: string[];
  href: string;
  links?: { href: string; label: string }[];
};

export const areas: Area[] = [
  {
    slug: "dashboard", label: "Tổng quan", group: "Tổng quan", href: "/dashboard",
    description: "Tình trạng nền và lối vào từng khu", roles: [], icon: LayoutDashboard, milestone: "R2",
    screens: ["Số liệu cấu hình nền", "Timer sắp tới hạn", "Lối vào các khu theo role"],
  },
  {
    slug: "line", label: "Xưởng", group: "Vận hành", href: "/area/line",
    description: "Chọn khu, lệnh đang chạy, nạp nguyên liệu, gọi hàng, giữ hàng", roles: ["line-operator", "line-lead"],
    icon: Factory, milestone: "R4–R6",
    screens: ["Chọn khu và ca", "Lệnh đang chạy", "Nạp lô vào buffer", "Gọi hàng", "Giữ hàng nghi lỗi", "Dựng lại pallet"],
  },
  {
    slug: "yard", label: "Cổng và dock", group: "Hậu cần", href: "/area/yard",
    description: "Đăng ký xe, kéo xe, nhận skid, chuyển skid", roles: ["yard"], icon: Truck, milestone: "R9",
    screens: ["Đăng ký xe ở cổng", "Yêu cầu kéo xe", "Nhận skid ở dock", "Chuyển skid giữa hai xe", "Bảng dỡ hàng"],
  },
  {
    slug: "finished-goods", label: "Thành phẩm", group: "Hậu cần", href: "/area/finished-goods",
    description: "Cất kho, đo cuối, kiểm pallet, picking, staging, chất xe", roles: ["shipping"], icon: PackageOpen,
    milestone: "R7–R8", screens: ["Cất kho", "Đo cuối EOLV", "Kiểm pallet", "Picking", "Staging", "Chất xe"],
  },
  {
    slug: "andon", label: "Gọi hàng", group: "Hậu cần", href: "/area/andon",
    description: "Bảng gọi hàng theo tier: gấp, trễ, đúng hạn", roles: ["logistics", "line-lead"], icon: Siren,
    milestone: "R10", screens: ["Bảng gọi hàng", "Nhận việc chở", "Quét lô", "Hoàn tất chặng"],
  },
  {
    slug: "bulk-nc", label: "NC hàng loạt", group: "Chất lượng", href: "/area/bulk-nc",
    description: "Tạo, giữ, hủy theo danh sách MTU", roles: ["quality"], icon: ListChecks, milestone: "R6",
    screens: ["Nạp danh sách MTU", "Giữ hàng loạt", "Hủy hàng loạt"],
  },
  {
    slug: "quality", label: "Kết luận NC", group: "Chất lượng", href: "/area/quality",
    description: "Kết luận tay, scrap tag, USE AS IS", roles: ["quality"], icon: ClipboardCheck, milestone: "R6",
    screens: ["Danh sách NC mở", "Kết luận NC", "In scrap tag"],
  },
  {
    slug: "containment", label: "Containment", group: "Chất lượng", href: "/area/containment",
    description: "Yêu cầu giữ hàng từ hệ chất lượng ngoài", roles: ["quality"], icon: ShieldAlert, milestone: "R6",
    screens: ["Danh sách yêu cầu", "Hàng trong containment", "Release, close"],
  },
  {
    slug: "disassembly", label: "Tháo rã", group: "Chất lượng", href: "/area/disassembly",
    description: "Lần cây thành phần, tháo, hoàn tác", roles: ["quality"], icon: Boxes, milestone: "R6",
    screens: ["Cây thành phần", "Tháo part", "Hoàn tác"],
  },
  {
    slug: "admin", label: "Quản trị", group: "Quản trị", href: "/admin/configuration-keys",
    description: "Configuration key, state machine, signal rule và timer", roles: ["mom-admin"], icon: Settings2,
    milestone: "R2", screens: ["Configuration key", "State machine", "Signal rule và timer"],
    links: [
      { href: "/admin/configuration-keys", label: "Configuration key" },
      { href: "/admin/state-machines", label: "State machine" },
      { href: "/admin/signal-rules", label: "Signal rule và timer" },
    ],
  },
];

export const groups: AreaGroup[] = ["Tổng quan", "Vận hành", "Chất lượng", "Hậu cần", "Quản trị"];

export function visibleAreas(roles: string[]): Area[] {
  const admin = roles.includes("mom-admin");
  return areas.filter((area) => admin || area.roles.length === 0 || area.roles.some((role) => roles.includes(role)));
}

/** Khu chứa đường dẫn hiện tại, để sidebar và breadcrumb biết đang ở đâu. */
export function areaOf(pathname: string): Area | undefined {
  return areas.find((a) => pathname === a.href || pathname.startsWith(`/area/${a.slug}`) || (a.slug === "admin" && pathname.startsWith("/admin")));
}

/** Role hệ thống của Keycloak không mang nghĩa nghiệp vụ: không hiện cho người dùng. */
export function businessRoles(roles: string[]): string[] {
  return roles.filter((r) => !r.startsWith("default-roles") && r !== "offline_access" && r !== "uma_authorization");
}
