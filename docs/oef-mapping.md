# Bảng cơ chế nền v2

Mỗi dòng là một cơ chế của nền kiểu Opcenter EX mà Foundation phải tái hiện theo hành vi đã quan sát
([scope §3.1](scope.md#31-cơ-chế-foundation-phải-tái-hiện)). Cột **Test** ghi tên test chứng minh
hành vi; chưa có test thì chưa tính là xong.

| Trạng thái | Nghĩa |
|---|---|
| `chưa làm` | Chưa có code |
| `đang làm` | Có code, thiếu test cho ít nhất một hành vi |
| `xong` | Mọi hành vi trong dòng có test xanh |

| Cơ chế | Mốc | Trạng thái | Test |
|---|---|---|---|
| Domain, `Query`/`ProjectionQuery`, analyzer chặn dùng nhầm model | R1 | `xong` | `Query_sees_submitted_changes_but_projection_query_does_not`, `Query_of_another_domain_is_refused`, `Pre_check_in_another_domain_is_a_model_error`, NVM004 (`Nvm004CrossDomainQueryAnalyzerTests`) |
| Cột hệ thống, khóa logic, ràng buộc bắt buộc ở tầng model | R1 | `xong` | `Mandatory_entity_property_is_enforced_by_the_model_while_the_column_stays_nullable`, `System_columns_are_stamped_from_the_platform_clock`, `Table_names_are_truncated_with_a_numeric_suffix_and_recorded` |
| Xóa mềm bằng số đếm `IsDeleted` | R1 | `xong` | `Delete_then_recreate_with_the_same_NId_succeeds`, `Sql_join_without_IsDeleted_counts_deleted_rows_lab_L03` |
| Facet trên entity của lớp Product | R1–R2 | `xong` | `Facets_of_an_entity_share_one_table_with_sparse_columns`; import vào POM: `Entity_not_imported_by_the_app_cannot_be_read_there` |
| Kiểm model ở service layer (lỗi `6400`) | R1 | `xong` | `Missing_mandatory_parameter_returns_6400_without_running_the_handler`, `Command_not_published_by_the_app_is_refused` |
| Pre-check theo app | R1 | `xong` | `Pre_check_blocks_only_in_the_app_that_enables_it` |
| Post-action trong cùng transaction, áp mọi app | R1 | `xong` | `Post_action_failure_fails_the_command_and_writes_nothing`, `Post_action_runs_in_every_app`, `Optional_post_action_can_be_disabled_in_the_solution` |
| Transaction: chuỗi trong FB và composite không nguyên tử | R1 | `xong` | `Composite_keeps_the_first_call_when_the_second_call_fails`, `Call_command_chain_in_a_block_rolls_back_the_parent_when_the_child_fails`, `Call_command_to_another_domain_commits_on_its_own`, `Commit_failure_publishes_no_event_and_keeps_nothing`, `Commit_failure_can_still_report_succeeded_for_lab_L02` |
| Dạng lỗi trả về (handler, service layer) | R1 | `xong` | `Handler_error_uses_the_error_and_succeeded_shape`, `Missing_mandatory_parameter_returns_6400_without_running_the_handler`, `Pre_check_blocks_only_in_the_app_that_enables_it` |
| Trace theo `CorrelationId` | R1 | `xong` | `Trace_filtered_by_correlation_id_holds_the_whole_chain`, `Trace_of_a_successful_call_contains_the_commit` |
| Event phát sau commit | R1–R2 | `xong` | `Event_is_stored_and_published_after_commit`, `Commit_failure_publishes_no_event_and_keeps_nothing`, `Plant_subscription_runs_the_handler_after_the_response`, `Relay_publishes_events_left_behind_while_the_bus_was_down` |
| Subscription, worker role | R2 | `xong` | `Event_without_subscription_is_published_and_nobody_handles_it_lab_L04`, `Plant_subscription_runs_the_handler_after_the_response`, `Worker_role_sets_how_many_consumers_take_events` |
| Reading function, reading model là view | R2 | `xong` | `Reading_function_returns_a_deferred_query_that_odata_keeps_filtering`, `Missing_view_gives_http_200_with_a_cut_body_lab_L15` |
| App, POM, OData theo app, token có hạn | R2 | `xong` | `Entity_set_applies_filter_order_top_and_count`, `Single_entity_is_read_by_its_id`, `Command_is_posted_by_its_published_name`, `Expired_token_is_refused_with_6401_lab_L05`, `Another_plant_in_the_url_is_not_served` |
| Extension app: tiền tố, phẳng | R2 | `xong` | `Extension_app_command_runs_under_the_base_app_with_its_prefix`, `Extension_app_cannot_extend_an_extension_app` |
| Signal rule, timer | R2 | `xong` | `Timer_rule_fires_once_per_interval_including_missed_ticks`, `One_shot_timer_fires_once_and_disables_itself`, `Event_rule_maps_event_properties_into_the_command`, `Event_rule_calls_a_command_when_the_event_arrives` |
| Configuration key | R2 | `xong` | `Configuration_key_is_stored_as_text_with_type_and_category` |
| State machine dạng dữ liệu, role theo transition | R2 | `xong` | `Status_added_as_data_is_usable_without_code`, `Transition_with_roles_needs_one_of_them_and_one_without_roles_is_open`, `Transition_bound_to_a_role_nobody_has_blocks_everyone_lab_L06`, `Roles_in_the_token_reach_the_state_machine` |
| Gửi tin ra ngoài đồng bộ và bất đồng bộ | R11 | `chưa làm` | |
| Gói deploy, cập nhật database, master data theo thứ tự | R12 | `chưa làm` | |

Test nằm ở `tests/Integration/Nvm.FoundationTests` (SQL Server, RabbitMQ thật qua Testcontainers), trừ NVM004.

## Giả định chưa xác minh trên hệ thật

Dựng theo scope §7: chỗ nào chưa quan sát được thì ghi ra đây, và nếu có hai cách hợp lý thì cả hai
chọn bằng cấu hình.

| Chỗ chưa rõ | Foundation làm | Cấu hình |
|---|---|---|
| Command con lỗi mà handler cha không kiểm `Succeeded`, vẫn trả thành công | Mặc định commit theo response của command gốc | `ChildFailureDoomsTransaction` |
| Commit hỏng sau khi handler xong | Mặc định response báo lỗi; biến thể giữ `Succeeded` của handler cho lab L02 | `CommitFailureReporting` |
| Pre-check có chạy khi command được gọi bằng `CallCommand` trong handler không | Không: pre-check bật theo app, lời gọi trong handler không qua app | — |
| Pre-check có chạy cho lời gọi con của composite không | Có: composite thuộc một app, lời gọi con mang app đó | — |
| Handler ném exception không bắt | Response lỗi dạng handler, `ErrorCode` = `HResult` của exception, lý do ở trace | — |
| Facet một dòng mỗi (entity, facet) hay một dòng mỗi entity | Một dòng mỗi (entity, facet), cột `FacetType` | — |
| Subscriber lỗi có được thử lại không | Không: ghi trace, rollback, xác nhận message | — |
| Subscription theo plant có hiệu lực ngay hay sau khởi động lại | Sau khi worker khởi động lại | — |
| Mã lỗi khi thiếu token (khác hết hạn) | `Unauthorized`; chỉ hết hạn mới là `6401` | — |
